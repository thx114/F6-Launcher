using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

public static partial class LauncherActionRunner
{
    private static readonly object NrPackImportGate = new();

    /// <summary>覆盖包 / CLI 为当前游戏选择 OptiScaler NR；下载只进入版本库。</summary>
    private static async Task<string> SetOptNrAsync(PackActionStep step, LauncherActionContext context)
    {
        if (context.GameId is not { } game)
            return "✗ set_opt_nr：没有当前游戏";

        bool useDefault = step.GetBool("default") == true;
        string? version = step.GetString("version")?.Trim();
        bool importFile = step.Raw.ValueKind == JsonValueKind.Object
            && (step.Raw.TryGetProperty("file", out _) || step.Raw.TryGetProperty("sha256", out _));
        if (useDefault && importFile)
            return "✗ set_opt_nr：包内 file/sha256 与 default=true 不能同时指定";
        if (useDefault && !string.IsNullOrEmpty(version))
            return "✗ set_opt_nr：version 与 default=true 不能同时指定";
        if (!useDefault && string.IsNullOrEmpty(version))
            return "✗ set_opt_nr：需要 version，或 default=true 恢复构建默认";
        if (!useDefault && !string.Equals(version, OptiScalerLibrary.Sanitize(version!), StringComparison.Ordinal))
            return "✗ set_opt_nr：version 必须是未经路径清理的安全目录名";

        var library = new OptiScalerLibrary(AppConfig.OptiScalerRootPath);
        string? id = AppConfig.GetSelectedOptiScalerId(game);
        var build = library.List(includeDirectorySize: false).Find(b => b.Id == id)
            ?? library.GetSelected(includeDirectorySize: false);
        if (build?.DllPath is not { } dll || !File.Exists(dll))
            return "✗ set_opt_nr：请先安装并选择 OptiScaler 构建（set_opt_build）";

        if (importFile)
        {
            try { ImportPackNr(step, context, version!); }
            catch (Exception ex) { return "✗ set_opt_nr：离线 NR 导入失败：" + ex.Message; }
        }
        else if (!useDefault)
        {
            var store = new DllVersionStore(AppConfig.CacheRoot);
            if (store.FilePath("dlssnr", version!, NrRuntimeLibrary.FileName).Length == 0)
            {
                if (step.GetBool("download") == false)
                    return "✗ set_opt_nr：NR 版本尚未下载：" + version;

                await RemoteCatalogService.RefreshIfDueAsync();
                RemoteCatalogService.ApplyDllFingerprints();
                // 自家 NR 已在指纹目录中，不必先等待无关的第三方 DLL 清单。
                var component = DllFingerprintCatalog.Packages.FirstOrDefault(c =>
                    c.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase) &&
                    c.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
                if (component is null)
                {
                    var catalog = await DllComponentCatalog.LoadAsync();
                    component = catalog.Of("dlssnr").FirstOrDefault(c =>
                        c.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
                }
                if (component is null)
                    return "✗ set_opt_nr：清单中没有 NR 版本：" + version;
                var progress = new Progress<DownloadProgress>(p => context.Report?.Invoke(
                    "下载 NR " + version + (p.Percent is { } percent ? $"… {percent:F0}%" : "…")));
                await NrRuntimeLibrary.DownloadVersionAsync(AppConfig.CacheRoot, component, progress);
            }
        }

        // 下载期间导航可能切换，但不能把旧动作应用到新上下文。
        if (context.IsContextCurrent?.Invoke() == false)
            return "✗ set_opt_nr：上下文已变化，未修改游戏选择";
        try { GameNrRuntimeService.SetOptiVersion(game, useDefault ? null : version); }
        catch (Exception ex) { return "✗ set_opt_nr：未保存游戏选择：" + ex.Message; }
        return $"已为 {game.GameBiz} 选择 OptiScaler NR：{(useDefault ? "构建默认" : version)}（重启游戏生效）";
    }

    /// <summary>离线分支不刷新清单、不下载；Archive 的返回计数不能代替落盘验证。</summary>
    private static void ImportPackNr(PackActionStep step, LauncherActionContext context, string version)
    {
        string? file = step.GetString("file");
        string? expected = step.GetString("sha256");
        if (string.IsNullOrWhiteSpace(file) || expected is not { Length: 64 } || !expected.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("需要 file 及完整的 64 位十六进制 sha256。");
        if (!Path.GetFileName(file).Equals(NrRuntimeLibrary.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("file 必须指向 nvngx_dlssnr.dll。");
        if (context.PackRoot is null && !Path.IsPathFullyQualified(file))
            throw new InvalidDataException("CLI 未提供 PackRoot 时 file 必须为绝对路径。");

        string source = ResolvePackFile(context, file)
            ?? throw new FileNotFoundException("找不到指定的离线 NR 文件。", file);
        if (context.PackRoot is not null)
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(context.PackRoot), Path.GetFullPath(source));
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part == ".."))
                throw new InvalidDataException("NR 文件不能逃出 PackRoot。");
        }
        RejectNrReparseAncestors(source);
        ValidatePackNr(source, version, expected);

        lock (NrPackImportGate)
        {
            var store = new DllVersionStore(AppConfig.CacheRoot);
            string directory = store.DirectoryFor("dlssnr", version);
            if (directory.Length == 0) throw new IOException("NR 版本库不可用。");
            string target = Path.Combine(directory, NrRuntimeLibrary.FileName);
            RejectNrReparseAncestors(target, allowMissing: true);
            if (File.Exists(target))
            {
                // 同标签不得改写异内容；相同内容重试可复用，避免覆写用户归档。
                ValidatePackNr(target, version, expected);
            }
            else
            {
                // 只允许既有 NR Archive 硬链接链路，不在这里添加任何复制兜底。
                if (store.Archive("dlssnr", version, [source]) == 0)
                    throw new IOException("NR 硬链接归档失败。");
            }

            string archived = store.FilePath("dlssnr", version, NrRuntimeLibrary.FileName);
            if (archived.Length == 0) throw new IOException("NR 归档文件缺失。");
            RejectNrReparseAncestors(archived);
            ValidatePackNr(archived, version, expected);
            string canonical = NrRuntimeLibrary.VersionFile(AppConfig.CacheRoot, version);
            RejectNrReparseAncestors(canonical);
            ValidatePackNr(canonical, version, expected);
            if (!HysxFileLink.AreHardLinked(archived, canonical))
                throw new IOException("NR 版本文件与内容库不是同一硬链接。");
        }
    }

    private static void ValidatePackNr(string path, string version, string expected)
    {
        string hash = NrRuntimeLibrary.Hash(path);
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NR SHA256 不符（已有同名版本时拒绝覆盖）。");
        using (var input = File.OpenRead(path))
        using (var reader = new PEReader(input))
        {
            var headers = reader.PEHeaders;
            if (headers.PEHeader?.Magic != PEMagic.PE32Plus || headers.CoffHeader.Machine != Machine.Amd64
                || (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0)
                throw new InvalidDataException("NR 必须是 PE x64 DLL。");
        }
        var fingerprint = DllFingerprintCatalog.Find("dlssnr", new FileInfo(path).Length, hash);
        if (!DllFingerprintCatalog.ValidatePe(path, fingerprint, out string error))
            throw new InvalidDataException("NR PE 验证失败：" + error);
        DllFingerprintCatalog.ValidateKnownFile("dlssnr", version, path, NrRuntimeLibrary.FileName);
    }

    private static void RejectNrReparseAncestors(string path, bool allowMissing = false)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("NR 路径及祖先目录不能是重解析点：" + current);
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }
}
