namespace HoYoShadeHub.Core.IO;

/// <summary>统一主程序与 RPC 的便携入口识别；新包只需 F6Launcher.exe，兼容已安装旧包。</summary>
public static class PortableLauncherLocation
{
    public static string? Find(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        foreach (string name in new[] { "F6Launcher.exe", "HoYoShadeHub.exe" })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
