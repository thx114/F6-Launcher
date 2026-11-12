using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Games;

public static class GameLaunchIniPreparation
{
    /// <summary>First launch and existing installs share one seed → package → bootstrap pass.</summary>
    public static GameIniBootstrapResult PrepareForLaunch(GameEntry entry, ShadeHost? host,
        Action syncPackage, Func<(bool GenshinFinalDx12, bool StarRailFinalDx12)> resolveRoute)
    {
        EnsureReShadeTarget(entry, host);
        syncPackage();
        var route = resolveRoute();
        return GameIniBootstrap.Ensure(entry, host, route.GenshinFinalDx12, route.StarRailFinalDx12);
    }

    // Only supply a missing target. Existing INI values remain for the package's own actions to replace.
    public static void EnsurePackActionTarget(GameEntry? entry, ShadeHost? host, IEnumerable<PackAutoAction> actions,
        Func<PackAutoAction, int, bool>? isStepEnabled = null)
    {
        bool needsReShade = actions.SelectMany(action => action.Steps.Where((_, index) => isStepEnabled?.Invoke(action, index) ?? true)).Any(step =>
            step.Action.Trim().ToLowerInvariant() is "set_addons" or "set_addon" or "apply_preset"
            || (PackIniAction.IsIniAction(step) && PackIniAction.UsesReShadeIni(step)));
        if (!needsReShade) return;
        EnsureReShadeTarget(entry, host);
    }

    public static string EnsureReShadeTarget(GameEntry? entry, ShadeHost? host)
    {
        string? target = entry?.ReShadeIniPath;
        if (target is not null && File.Exists(target)) return target;
        if (string.IsNullOrWhiteSpace(target))
            throw new IOException("无法准备 ReShade.ini：当前游戏目录未确定。");
        if (host is null || (!File.Exists(host.ReShadeIniPath) && !host.Exists))
            throw new IOException($"无法生成 {target}：没有可用的 ReShade 宿主或模板（{host?.ReShadeIniPath ?? "未指定"}）。");
        string directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"游戏目录不存在：{directory}");
        string temp = Path.Combine(directory, ".f6-reshade-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (File.Exists(host.ReShadeIniPath))
                File.Copy(host.ReShadeIniPath, temp, overwrite: false);
            else
            {
                // A fresh/portable host may have its DLL and plugins but no generated template yet.
                // Seed the game directly; do not require running the game or modifying the shared host.
                var ini = IniDocument.CreateEmpty();
                ini.SetValue("GENERAL", "EffectSearchPaths", Path.Combine(host.RootPath, "reshade-shaders", "Shaders", "**"));
                ini.SetValue("GENERAL", "TextureSearchPaths", Path.Combine(host.RootPath, "reshade-shaders", "Textures", "**"));
                ini.SetValue("ADDON", "AddonPath", host.AddonsPath);
                ini.SetValue("INPUT", "KeyOverlay", "36,0,0,0");
                ini.SetValue("INPUT", "GamepadNavigation", "0");
                ini.SetValue("OVERLAY", "TutorialProgress", GameIniBootstrap.TutorialDoneValue);
                ini.Save(temp);
            }
            try { File.Move(temp, target, overwrite: false); }
            catch (IOException) when (File.Exists(target)) { }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return target;
    }
}
