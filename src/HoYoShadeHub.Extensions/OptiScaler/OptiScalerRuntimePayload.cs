namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 运行目录只接收构建载荷，不传播构建/运行过程中产生的诊断、备份或临时文件。
/// 不按 DLL 白名单裁剪，保留插件、模型、配置和组件自带的数据文件。
/// </summary>
internal static class OptiScalerRuntimePayload
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".vs", "backup", "backups", "profile-recovery",
        "GenshinCapture", "current-output-captures", "log", "logs", "dumps", "crashdumps",
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dmp", ".mdmp", ".log", ".jsonl", ".pdb", ".pending", ".tmp", ".bak",
    };

    internal static bool IsExcludedDirectory(string name) => ExcludedDirectories.Contains(name)
        || name.StartsWith("backup-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".backup-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".deploy-backup-", StringComparison.OrdinalIgnoreCase);

    internal static bool IsExcludedFile(string name) => ExcludedExtensions.Contains(Path.GetExtension(name))
        || name.StartsWith("OptiScaler_crash", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".nr-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("nr-present-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".hub-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".build.before-", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".before-", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".backup", StringComparison.OrdinalIgnoreCase);

    /// <summary>先收集并验证整份载荷；排除的目录不进入，载荷中的重解析点则拒绝部署。</summary>
    internal static IReadOnlyList<string> Collect(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        RejectReparsePoint(root);
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetFileName(entry);
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IsExcludedDirectory(name)) continue;
                    RejectReparsePoint(entry, attributes);
                    pending.Push(entry);
                }
                else
                {
                    if (IsExcludedFile(name)) continue;
                    RejectReparsePoint(entry, attributes);
                    files.Add(entry);
                }
            }
        }
        return files;
    }

    private static void RejectReparsePoint(string path, FileAttributes? attributes = null)
    {
        if (((attributes ?? File.GetAttributes(path)) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("OptiScaler 运行载荷包含不支持的重解析点：" + path);
    }
}
