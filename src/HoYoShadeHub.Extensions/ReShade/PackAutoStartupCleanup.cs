namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>仅由主启动器启动入口调用；不注册监视器，不参与覆盖包安装。</summary>
public static class PackAutoStartupCleanup
{
    public static int DeleteExisting(params string?[] roots)
    {
        int deleted = 0;
        foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r))
                     .Select(r => Path.GetFullPath(r!)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            // 不跟随目录链接，也不触碰目录外的包或游戏。
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out string? directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!Path.GetFileName(file).Equals(GameAddonPackUserContent.AutoActionFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    // 失败必须阻止启动，不能带着仍可触发的旧动作继续加载页面。
                    File.Delete(file);
                    deleted++;
                }
                foreach (string child in Directory.EnumerateDirectories(directory))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
        }
        return deleted;
    }
}
