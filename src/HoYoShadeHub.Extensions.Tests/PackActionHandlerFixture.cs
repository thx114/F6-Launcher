using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>环境端口；不链接 WinUI/AppConfig 单例，也不读取用户数据库。</summary>
public sealed class LauncherActionContext
{
    public GameId? GameId { get; init; }
    public GameBiz? GameBiz { get; init; }
    public GameEntry? Entry { get; init; }
    public ShadeHost? Host { get; init; }
    public GamePluginService? Plugins { get; init; }
    public string? PackRoot { get; init; }
}

/// <summary>
/// 编译链接生产 OptConfig partial；只提供其环境/上下文，不复制任何 handler。
/// 配置写入端口落在 fixture 字典，预设、profile、main 的执行和冲突处理使用真实服务。
/// </summary>
public static partial class LauncherActionRunner
{
    private static GameId? ResolveGameId(LauncherActionContext context) => context.GameId
        ?? (context.GameBiz is { } biz ? GameId.FromGameBiz(biz) : null);
    private static string? ResolvePackFile(LauncherActionContext context, string file)
        => PackLocalActionExecutor.ResolvePackFile(new() { PackRoot = context.PackRoot }, file);

    internal static string ExecuteOptConfigFixture(PackActionStep step, LauncherActionContext context)
        => step.Action.Trim().ToLowerInvariant() switch
        {
            "import_opt_config" => ImportOptConfig(step, context),
            "update_opt_config" => UpdateOptConfig(step, context),
            "set_opt_config" or "apply_opt_config" or "select_opt_config" => SetOptConfig(step, context),
            _ => throw new NotSupportedException(step.Action),
        };
}
