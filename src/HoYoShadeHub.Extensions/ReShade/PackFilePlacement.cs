using System.Security.Cryptography;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>One explicitly declared package file → one destination. Never writes through an existing hardlink.</summary>
public static class PackFilePlacement
{
    public sealed record Plan(string Source, string Destination, string? Sha256, bool Overwrite);
    public sealed record Result(string Destination, string Sha256, string? Backup, bool Changed);

    public static bool IsFilePlacement(PackActionStep step) =>
        step.Action.Trim().ToLowerInvariant() is "copy_file" or "place_file";

    public static Plan Resolve(PackActionStep step, string? packRoot, string? gameDirectory,
        string? launcherDirectory, string? shadeDirectory)
    {
        if (string.IsNullOrWhiteSpace(packRoot)) throw new InvalidDataException("放置文件需要覆盖包根目录。");
        string source = PackageTree.ResolveRelative(packRoot, step.GetString("file")
            ?? throw new InvalidDataException("copy_file 缺少包内 file。"));
        ValidatePath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("包内文件不存在。", source);
        string directory = step.GetString("directory") ?? throw new InvalidDataException("copy_file 缺少 directory。");
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("directory 不能为空。");
        string targetDirectory;
        if (Path.IsPathFullyQualified(directory)) targetDirectory = Path.GetFullPath(directory);
        else
        {
            string? root = (step.GetString("target") ?? "game").Trim().ToLowerInvariant() switch
            {
                "game" => gameDirectory,
                "launcher" => launcherDirectory,
                "shade" => shadeDirectory,
                _ => throw new InvalidDataException("相对 directory 的 target 必须为 game / launcher / shade。"),
            };
            if (string.IsNullOrWhiteSpace(root)) throw new DirectoryNotFoundException("找不到目标根目录。");
            targetDirectory = directory == "." ? Path.GetFullPath(root) : PackageTree.ResolveRelative(root, directory);
        }
        ValidatePath(targetDirectory);
        string name = step.GetString("name") ?? Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != name.TrimEnd('.', ' ')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("name 必须是文件名，不能包含目录。");
        string destination = PackageTree.ResolveRelative(targetDirectory, name);
        ValidatePath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("源文件与目标文件相同。");
        string? sha256 = step.GetString("sha256");
        if (sha256 is not null && (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("sha256 必须是 64 位十六进制摘要。");
        return new(source, destination, sha256, step.GetBool("overwrite") ?? true);
    }

    public static Result Apply(Plan plan, string packRoot)
    {
        ValidatePath(plan.Source);
        ValidatePath(plan.Destination);
        string sourceHash = Hash(plan.Source);
        if (plan.Sha256 is { } expected && !sourceHash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("包内文件 SHA256 不匹配；未写入目标。");
        bool exists = File.Exists(plan.Destination);
        string? previousHash = exists ? Hash(plan.Destination) : null;
        bool neuralRuntime = HysxFileLink.IsNeuralRuntime(plan.Source) || HysxFileLink.IsNeuralRuntime(plan.Destination);
        if (previousHash == sourceHash && (!neuralRuntime || HysxFileLink.AreHardLinked(plan.Source, plan.Destination)))
            return new(plan.Destination, sourceHash, null, false);
        if (exists && !plan.Overwrite) throw new IOException("目标已存在且 overwrite=false；未覆盖：" + plan.Destination);
        string directory = Path.GetDirectoryName(plan.Destination)!;
        Directory.CreateDirectory(directory);
        string staged = Path.Combine(directory, ".f6-place-" + Guid.NewGuid().ToString("N") + ".tmp");
        string? backup = null;
        try
        {
            // NR keeps the launcher's strict same-volume hardlink contract; no silent copy fallback.
            if (neuralRuntime) HysxFileLink.LinkNeuralRuntime(plan.Source, staged);
            else File.Copy(plan.Source, staged, overwrite: false);
            if (Hash(staged) != sourceHash) throw new IOException("暂存文件内容校验失败。");
            if (exists)
            {
                string backups = PackageTree.ResolveRelative(packRoot, ".file-backups");
                ValidatePath(backups);
                Directory.CreateDirectory(backups);
                backup = PackageTree.ResolveRelative(backups, Guid.NewGuid().ToString("N") + ".bak");
                File.Copy(plan.Destination, backup, overwrite: false);
                if (Hash(backup) != previousHash) throw new IOException("目标在备份时发生变化；未覆盖。");
                File.WriteAllText(backup + ".json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    destination = plan.Destination, sha256 = previousHash, createdUtc = DateTimeOffset.UtcNow,
                }));
            }
            ValidatePath(plan.Destination);
            if (File.Exists(plan.Destination) != exists || (exists && Hash(plan.Destination) != previousHash))
                throw new IOException("目标文件已被其他程序修改；未覆盖。");
            File.Move(staged, plan.Destination, overwrite: exists && plan.Overwrite);
            if (Hash(plan.Destination) != sourceHash
                || (neuralRuntime && !HysxFileLink.AreHardLinked(plan.Source, plan.Destination)))
                throw new IOException("放置文件后的内容或 NR 硬链接校验失败。");
            return new(plan.Destination, sourceHash, backup, true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void ValidatePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\?\\", StringComparison.Ordinal) || full.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new InvalidDataException("不支持设备路径。");
        for (string? part = full; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("文件路径包含重解析点：" + part);
    }
}
