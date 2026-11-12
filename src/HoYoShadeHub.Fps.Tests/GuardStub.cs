// 独立 fixture 不运行启动器的后台进程守卫。
namespace HoYoShadeHub.Features.GameLauncher;
internal static class MultiplayerGameGuard
{
    internal static bool IsActive => false;
    internal static string BlockReason() => "fixture";
}
