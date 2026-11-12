using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>覆盖包安装后的路径绑定；只准备缺失的 INI 并修改 AddonPath，不执行包内动作。</summary>
public static class GamePackIniBinding
{
    public static string Bind(GameEntry entry, ShadeHost? host, string addonDirectory)
    {
        string fullDirectory = Path.GetFullPath(addonDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // 先确认插件已铺好，避免失败导入留下指向空目录的 INI。
        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException($"无法绑定覆盖包：插件包目录不存在：{fullDirectory}");

        string iniPath = GameLaunchIniPreparation.EnsureReShadeTarget(entry, host);
        ReShadeProfile profile = ReShadeProfile.Load(iniPath);
        if (!string.Equals(profile.AddonPath, fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            profile.SetAddonPath(fullDirectory);
            profile.Save();
        }

        // 导入入口必须得到真实落盘结果，文件占用/写入失败不能被当成安装成功。
        if (!string.Equals(ReShadeProfile.Load(iniPath).AddonPath, fullDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"覆盖包已安装，但无法绑定 {iniPath} 的 AddonPath → {fullDirectory}");
        return fullDirectory;
    }
}
