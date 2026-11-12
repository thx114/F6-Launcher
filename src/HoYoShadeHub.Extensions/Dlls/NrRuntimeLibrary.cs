using System.Security.Cryptography;
using HoYoShadeHub.Extensions.Services;
using System.Text.Json;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>NR 内容寻址库；同内容、同卷的所有受管位置共用一个文件。</summary>
public static class NrRuntimeLibrary
{
    public const string FileName = "nvngx_dlssnr.dll";
    private static readonly object RegistrationGate = new();
    private sealed record VersionRegistration(string Version, string Sha256);

    // 内容去重不代表用户收藏。只有显式归档才留下独立记账，构建默认 NR 的 Intern 不写。
    internal static void RememberVersion(string cacheRoot, string version, string canonical)
    {
        lock (RegistrationGate)
        {
            string hash = Path.GetFileName(Path.GetDirectoryName(canonical)!);
            string path = RegistrationPath(cacheRoot, version);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new VersionRegistration(version, hash)));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static void ForgetVersion(string cacheRoot, string version)
    {
        lock (RegistrationGate) File.Delete(RegistrationPath(cacheRoot, version));
    }

    private static string RegistrationPath(string cacheRoot, string version) =>
        Path.Combine(cacheRoot, "nr-version-index", OptiScalerLibrary.Sanitize(version) + ".json");

    public static IReadOnlyList<string> SelectableVersions(IEnumerable<string> installed, Func<string, bool> enabled)
        => installed.Where(enabled).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Hash(string path)
        => Hash(path, CancellationToken.None);

    private static string Hash(string path, CancellationToken cancellationToken)
    {
        using var input = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static string Intern(string cacheRoot, string source)
        => Intern(cacheRoot, source, Hash(source));

    private static string Intern(string cacheRoot, string source, string hash, CancellationToken cancellationToken = default)
    {
        string canonical = Path.Combine(cacheRoot, "nr-content", hash, FileName);
        // Same NTFS file means the hash just computed also validates the canonical
        // bytes. A separate canonical file still requires its own content check.
        if (File.Exists(canonical) && !HysxFileLink.AreHardLinked(source, canonical)
            && !Hash(canonical, cancellationToken).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("NR 内容库 SHA256 校验失败：" + canonical);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(canonical)) HysxFileLink.LinkNeuralRuntime(source, canonical);
        HysxFileLink.LinkNeuralRuntime(canonical, source);
        return canonical;
    }

    public static string VersionFile(string cacheRoot, string version)
    {
        string path = new DllVersionStore(cacheRoot).FilePath("dlssnr", version, FileName);
        if (path.Length == 0) throw new FileNotFoundException("NR 版本归档缺失，请先在 DLL 配置中安装：" + version);
        string hash = Hash(path);
        DllFingerprintCatalog.ValidateKnownHash("dlssnr", version, path, hash);
        return Intern(cacheRoot, path, hash);
    }

    /// <summary>仅恢复显式归档的版本；内容缓存也含默认库和旧构建，不能据此生成用户版本。</summary>
    public static IReadOnlyList<string> RecoverContentVersions(string cacheRoot)
    {
        lock (RegistrationGate) return RecoverRegisteredVersions(cacheRoot);
    }

    private static IReadOnlyList<string> RecoverRegisteredVersions(string cacheRoot)
    {
        var errors = new List<string>();
        string indexRoot = Path.Combine(cacheRoot, "nr-version-index");
        if (!Directory.Exists(indexRoot)) return errors;
        if ((File.GetAttributes(indexRoot) & FileAttributes.ReparsePoint) != 0) return errors;
        var store = new DllVersionStore(cacheRoot);
        foreach (string registrationPath in Directory.EnumerateFiles(indexRoot, "*.json"))
        {
            try
            {
                if ((File.GetAttributes(registrationPath) & FileAttributes.ReparsePoint) != 0) continue;
                var registration = JsonSerializer.Deserialize<VersionRegistration>(File.ReadAllText(registrationPath));
                if (registration is null || string.IsNullOrWhiteSpace(registration.Version)
                    || registration.Sha256 is not { Length: 64 } || !registration.Sha256.All(char.IsAsciiHexDigit)
                    || !Path.GetFullPath(registrationPath).Equals(Path.GetFullPath(RegistrationPath(cacheRoot, registration.Version)), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("NR 版本记账无效。");
                string source = Path.Combine(cacheRoot, "nr-content", registration.Sha256, FileName);
                string directory = Path.GetDirectoryName(source)!;
                if (!File.Exists(source)) throw new FileNotFoundException("已归档 NR 的内容文件缺失。", source);
                if ((File.GetAttributes(Path.GetDirectoryName(directory)!) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("NR 内容库不能使用重解析点。");
                string hash = Hash(source);
                if (!hash.Equals(registration.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("NR 内容目录与 SHA256 不符。");
                string label = registration.Version;
                string target = store.FilePath("dlssnr", label, FileName);
                if (target.Length > 0)
                {
                    if (!Hash(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("NR 版本与记账内容冲突，保留当前版本。");
                    continue;
                }
                var fingerprint = DllFingerprintCatalog.Find("dlssnr", new FileInfo(source).Length, hash);
                if (!DllFingerprintCatalog.ValidatePe(source, fingerprint, out string error)) throw new IOException(error);
                string destination = Path.Combine(store.DirectoryFor("dlssnr", label), FileName);
                HysxFileLink.LinkNeuralRuntime(source, destination);
                target = store.FilePath("dlssnr", label, FileName);
                if (target.Length == 0 || !HysxFileLink.AreHardLinked(source, target))
                    throw new IOException("恢复 NR 版本硬链接失败。");
            }
            catch (Exception ex) { errors.Add(registrationPath + ": " + ex.Message); }
        }
        return errors;
    }

    /// <summary>下载到版本库，不改变任何游戏或共享目录当前加载的 NR。</summary>
    public static async Task<string> DownloadVersionAsync(string cacheRoot, DllComponent component,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!component.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不是 NR 下载条目。", nameof(component));
        string temporary = Path.Combine(cacheRoot, "nr-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await DllInstaller.InstallAsync(temporary, component, progress, cancellationToken);
            if (!result.Ok) throw new IOException(result.Error ?? "NR 下载失败。");
            string source = Path.Combine(temporary, FileName);
            if (!File.Exists(source)) throw new IOException("下载包缺少 nvngx_dlssnr.dll。");
            if (!DllFingerprintCatalog.ValidatePe(source, null, out string error)) throw new IOException(error);
            string hash = Hash(source);
            new DllVersionStore(cacheRoot).Archive("dlssnr", component.Version, [source]);
            string archived = VersionFile(cacheRoot, component.Version);
            if (!Hash(archived).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("NR 归档校验失败。");
            return archived;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public static string OptiDirectory(string cacheRoot, string gameKey, string buildId) =>
        Path.Combine(GameAddonPack.PackDirectory(cacheRoot, gameKey), "OptiRuntime",
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(buildId)))[..16]);

    /// <summary>独立加载目录避免共享构建被不同游戏的 NR 选择互相覆盖；配置独立、DLL 硬链接。</summary>
    public static string PrepareOpti(string cacheRoot, string gameKey, string buildId, string dllPath, string? version)
    {
        dllPath = Path.GetFullPath(dllPath);
        string sourceRoot = Path.GetDirectoryName(dllPath)!;
        string targetRoot = OptiDirectory(cacheRoot, gameKey, buildId);
        // 一次验证同一份载荷，两个复制/NR 绑定阶段遵守相同范围；不得把备份内 NR 改成当前版本。
        IReadOnlyList<string> payload = OptiScalerRuntimePayload.Collect(sourceRoot);
        if (!payload.Contains(dllPath, StringComparer.OrdinalIgnoreCase))
            throw new IOException("OptiScaler 主 DLL 不在可部署的运行载荷中：" + dllPath);
        string defaultNr = Path.Combine(sourceRoot, FileName);
        string nr = version is not null ? VersionFile(cacheRoot, version)
            : File.Exists(defaultNr) ? Intern(cacheRoot, defaultNr)
            : throw new FileNotFoundException("构建默认 NR 缺失，请先在全局插件中补齐。", defaultNr);
        foreach (string source in payload)
        {
            string relative = Path.GetRelativePath(sourceRoot, source);
            if (HysxFileLink.IsNeuralRuntime(source)) continue;
            string target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Path.GetExtension(source).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (!HysxFileLink.AreHardLinked(source, target)) HysxFileLink.LinkOrCopy(source, target);
            }
            else if (!File.Exists(target)) File.Copy(source, target);
        }
        HysxFileLink.LinkNeuralRuntime(nr, Path.Combine(targetRoot, FileName));
        // 某些构建从组件子目录加载 NR，同时绑定，避免落回包自带版本。
        foreach (string source in payload.Where(HysxFileLink.IsNeuralRuntime))
            HysxFileLink.LinkNeuralRuntime(nr, Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, source)));
        return Path.Combine(targetRoot, Path.GetFileName(dllPath));
    }

    /// <summary>显式刷新/安装时整合启动器受管库，逐文件报告失败，不接触游戏进程。</summary>
    public static IReadOnlyList<string> Consolidate(string cacheRoot, params string?[] roots)
        => ConsolidateScoped(cacheRoot, roots);

    public sealed record ConsolidationProgress(int Completed, int Total, string File);

    /// <summary>Installation supplies only its changed directories. Manual full-library maintenance remains explicit.</summary>
    public static IReadOnlyList<string> ConsolidateScoped(string cacheRoot, IEnumerable<string?> roots,
        IProgress<ConsolidationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var normalized = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r!)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in normalized.Where(root => !normalized.Any(parent => root.Length > parent.Length
            && root.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("NR 整理目录不能是重解析点：" + root);
                foreach (string file in Directory.EnumerateFiles(root, FileName, new EnumerationOptions
                    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(file);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(root + ": " + ex.Message); }
        }
        int completed = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(completed, files.Count, file));
            try { Intern(cacheRoot, file, Hash(file, cancellationToken), cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(file + ": " + ex.Message); }
            progress?.Report(new(++completed, files.Count, file));
        }
        return errors;
    }
}
