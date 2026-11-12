namespace HoYoShadeHub.Features.Plugins;

public static partial class LauncherActionRunner
{
    /// <summary>
    /// 清空当前游戏的附加启动项和模块选择，然后由覆盖包显式启用所需项。
    /// 不修改其他游戏、全局模块开关、驱动配置或游戏启动参数。
    /// </summary>
    private static string ResetLaunchOptions(LauncherActionContext context)
    {
        if (context.GameId is not { } game)
            return "✗ reset_launch_options：没有当前游戏";

        AppConfig.SetUseXxmiInjectLaunchOption(game, false);
        AppConfig.SetUseRocketLaunchOption(game, false);
        AppConfig.SetUseStarwardLaunchOption(game, false);
        AppConfig.SetUseHoYoShadeLaunchOption(game, false);
        AppConfig.SetUseOpenHoYoShadeLaunchOption(game, false);
        AppConfig.SetUseFpsUnlockLaunchOption(game, false);
        AppConfig.SetUseModulesLaunchOption(game, false);
        AppConfig.SetUseOptiScalerLaunchOption(game, false);
        AppConfig.SetLaunchGenshinBlenderPluginOption(game, false);
        AppConfig.SetLaunchZZZBlenderPluginOption(game, false);
        AppConfig.SetUsedModuleKeys(game, []);
        AppConfig.SetEnableGameLaunchOption(game, true);
        return "已清空当前游戏的附加启动项和模块选择";
    }
}
