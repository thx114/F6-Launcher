using HoYoShadeHub.Extensions.ReShade;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

public static partial class LauncherActionRunner
{
    private static bool IsLauncherVersionRequirement(PackActionStep step) =>
        step.Action.Trim().ToLowerInvariant() is "require_launcher_version" or "request_launcher_version";

    private static string CheckLauncherVersion(PackActionStep step)
        => PackLocalActionExecutor.CheckLauncherVersion(step, AppConfig.AppVersion);

    /// <summary>只读前置条件每次重查；不能被 once success 跳过，也不能晚于同包其他动作。</summary>
    private static async Task<PackActionOncePolicy.ExecutionResult?> CheckLauncherRequirementsAsync(
        IEnumerable<PackAutoAction> actions, LauncherActionContext context,
        Func<PackAutoAction, int, bool>? isStepEnabled = null)
    {
        foreach (PackActionStep step in actions.SelectMany(action => action.Steps
            .Where((_, index) => isStepEnabled?.Invoke(action, index) ?? true)).Where(IsLauncherVersionRequirement))
        {
            string result = CheckLauncherVersion(step);
            if (PackActionOncePolicy.IsSuccessfulOutcome(result)) continue;
            context.Report?.Invoke(result);
            if (context.Interactive && context.XamlRoot is not null)
            {
                try
                {
                    await new ContentDialog
                    {
                        XamlRoot = context.XamlRoot,
                        Title = "请更新启动器",
                        Content = result.TrimStart('✗', ' ') + "\n\n可前往设置的关于页面，在 GitHub 版本列表中选择新版本下载安装。",
                        CloseButtonText = "知道了",
                    }.ShowAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Launcher version requirement prompt");
                }
            }
            return PackActionOncePolicy.Block(result, PackActionOncePolicy.BlockReason.RequirementNotMet);
        }
        return null;
    }
}
