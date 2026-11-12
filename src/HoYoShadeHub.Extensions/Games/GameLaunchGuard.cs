namespace HoYoShadeHub.Extensions.Games;

public sealed record GameLaunchConflict(int Pid, string ProcessName, GameProcessInspection Inspection)
{
    public string Message => Inspection.Status == GameProcessStatus.Unknown
        ? $"发现 {ProcessName}（PID {Pid}），无法确认是否已退出，已阻止重复启动。{Inspection.Detail}"
        : $"{ProcessName}（PID {Pid}）正在运行，已阻止重复启动。";
}

public sealed class GameAlreadyRunningException(GameLaunchConflict conflict) : InvalidOperationException(conflict.Message)
{
    public GameLaunchConflict Conflict { get; } = conflict;
}

/// <summary>Creation must be guarded independently of asynchronous UI state refresh.</summary>
public static class GameLaunchGuard
{
    public static GameLaunchConflict? FindConflict(string executableName)
    {
        var match = GameProcessDiscovery.FindBlocking(executableName);
        if (match is null) return null;
        using var process = match.Process;
        return new(process.Id, process.ProcessName + ".exe", match.Inspection);
    }

    public static void EnsureCanStart(string executableName)
    {
        if (FindConflict(executableName) is { } conflict) throw new GameAlreadyRunningException(conflict);
    }

    public static T Start<T>(string executableName, Func<T> create,
        Func<GameLaunchConflict?>? inspect = null)
    {
        // Inspection errors propagate: no failure path may fall through to creation.
        if ((inspect ?? (() => FindConflict(executableName)))() is { } conflict)
            throw new GameAlreadyRunningException(conflict);
        return create();
    }
}
