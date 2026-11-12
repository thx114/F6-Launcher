using System.ComponentModel;
using System.Diagnostics;

namespace HoYoShadeHub.Extensions.Games;

public enum GameProcessStatus { NotRunning, Running, Unknown }

public readonly record struct GameProcessInspection(GameProcessStatus Status, string? Detail = null)
{
    public bool BlocksLaunch => Status is GameProcessStatus.Running or GameProcessStatus.Unknown;
}

/// <summary>The caller owns Process, including when transferring it to UI tracking.</summary>
public sealed record GameProcessMatch(Process Process, GameProcessInspection Inspection);

/// <summary>Shared by launch guards, running-state UI and injection target discovery.</summary>
public static class GameProcessDiscovery
{
    private static string NormalizeProcessName(string executableName)
    {
        string name = Path.GetFileName(executableName.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The caller owns the returned process; all other candidates are disposed.</summary>
    public static Process? FindRunning(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName)) return null;
        using var current = Process.GetCurrentProcess();
        return FindRunning(Process.GetProcessesByName(NormalizeProcessName(executableName)), current.SessionId);
    }

    public static Process? FindRunning(IEnumerable<Process> candidates, int sessionId,
        IReadOnlyCollection<int>? excludedProcessIds = null)
        => Select(candidates, sessionId, includeUnknown: false, excludedProcessIds)?.Process;

    /// <summary>Permission/state uncertainty blocks creation, but is not an injection target.</summary>
    public static GameProcessMatch? FindBlocking(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        using var current = Process.GetCurrentProcess();
        return Select(Process.GetProcessesByName(NormalizeProcessName(executableName)),
            current.SessionId, includeUnknown: true);
    }

    private static GameProcessMatch? Select(IEnumerable<Process> candidates, int sessionId,
        bool includeUnknown, IReadOnlyCollection<int>? excludedProcessIds = null)
    {
        GameProcessMatch? selected = null;
        foreach (var process in candidates)
        {
            GameProcessInspection inspection = Inspect(process, sessionId);
            if ((inspection.Status == GameProcessStatus.Running || includeUnknown && inspection.BlocksLaunch)
                && !(excludedProcessIds?.Contains(process.Id) ?? false)
                && (selected is null || selected.Inspection.Status == GameProcessStatus.Unknown
                    && inspection.Status == GameProcessStatus.Running))
            {
                selected?.Process.Dispose();
                selected = new(process, inspection);
            }
            else process.Dispose();
        }
        return selected;
    }

    /// <summary>Unverifiable processes remain tracked until exit is positively established.</summary>
    public static bool IsRunning(Process process, int sessionId)
        => Inspect(process, sessionId).BlocksLaunch;

    public static GameProcessInspection Inspect(Process process, int sessionId)
        => InspectCore(() => { process.Refresh(); return process.SessionId; },
            () => process.Threads.Count, () => process.HasExited, sessionId);

    internal static GameProcessInspection InspectCore(Func<int> readSession, Func<int> readThreadCount,
        Func<bool> readExited, int sessionId)
    {
        try
        {
            if (readSession() != sessionId) return new(GameProcessStatus.NotRunning);
            // A successfully observed process with no threads is a retained exit record, not a launch conflict.
            if (readThreadCount() == 0) return new(GameProcessStatus.NotRunning);
            return new(readExited() ? GameProcessStatus.NotRunning : GameProcessStatus.Running);
        }
        catch (InvalidOperationException) { return new(GameProcessStatus.NotRunning); }
        catch (Win32Exception ex) { return new(GameProcessStatus.Unknown, $"Win32 {ex.NativeErrorCode}: {ex.Message}"); }
    }
}
