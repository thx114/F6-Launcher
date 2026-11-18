using System.Diagnostics;

namespace HoYoShadeHub.Features.GameLauncher;

internal static class MultiplayerGameGuard
{
    internal static bool IsActive
    {
        get
        {
            var processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    try { if (F6Launcher.Background.MultiplayerProcessNames.All.Contains(process.ProcessName)) return true; }
                    catch { }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            return false;
        }
    }
    internal static string BlockReason() => "检测到多人游戏，已停止帧率解锁。";
}
