using System.Diagnostics;
using System.ComponentModel;
using System.Reflection;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class GameProcessDiscoveryTests
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        Check(GameProcessDiscovery.FindRunning(current.ProcessName) is not null,
            "dotted process names remain intact without an exe suffix");
        Check(GameProcessDiscovery.FindRunning(current.ProcessName + ".exe") is not null,
            "exe suffix is normalized without stripping dotted names");
        Check(GameProcessDiscovery.FindRunning(null) is null, "empty target");
        Check(GameProcessDiscovery.FindRunning("f6-no-such-process-" + Guid.NewGuid().ToString("N")) is null, "absent target");
        Check(GameProcessDiscovery.IsRunning(current, session), "live process without a game window");
        Check(!GameProcessDiscovery.IsRunning(current, session + 1), "foreign session excluded");
        var unknown = GameProcessDiscovery.InspectCore(() => session, () => 1,
            () => throw new Win32Exception(5), session);
        Check(unknown.Status == GameProcessStatus.Unknown && unknown.BlocksLaunch,
            "access denied with threads is unknown and blocks launch");
        Check(GameProcessDiscovery.InspectCore(() => session, () => 0,
                () => throw new Exception("zero-thread exit record must not query exit status"), session)
                .Status == GameProcessStatus.NotRunning,
            "confirmed zero-thread process record is treated as exited");
        Check(GameProcessDiscovery.InspectCore(() => session, () => 1, () => false, session).Status == GameProcessStatus.Running,
            "live process with threads is detected without requiring a game window");
        Check(GameProcessDiscovery.InspectCore(() => session, () => 1, () => true, session).Status == GameProcessStatus.NotRunning,
            "positive exit releases block");
        Check(GameProcessDiscovery.InspectCore(() => throw new Win32Exception(5), () => 1, () => false, session).BlocksLaunch,
            "session query failure cannot permit creation");
        Check(GameProcessDiscovery.InspectCore(() => session + 1, () => throw new Exception("must not read threads"),
                () => false, session)
            .Status == GameProcessStatus.NotRunning, "foreign session does not query protected exit status");
        int created = 0;
        foreach (var inspection in new[] { unknown, new GameProcessInspection(GameProcessStatus.Running) })
        {
            bool blocked = false;
            try { GameLaunchGuard.Start("YuanShen.exe", () => ++created, () => new(42, "YuanShen.exe", inspection)); }
            catch (GameAlreadyRunningException ex) { blocked = ex.Conflict.Pid == 42; }
            Check(blocked && created == 0, $"{inspection.Status} blocks the actual creation callback");
        }
        bool queryFailure = false;
        try { GameLaunchGuard.Start("YuanShen.exe", () => ++created, () => throw new IOException("probe failed")); }
        catch (IOException) { queryFailure = true; }
        Check(queryFailure && created == 0, "probe exception cannot fall through to process creation");
        Check(GameLaunchGuard.Start("unused", () => ++created, () => null) == 1,
            "clear inspection invokes creation once");
        using (var named = GameProcessDiscovery.FindRunning(current.ProcessName + ".exe"))
            Check(named is not null, "exe name normalized");

        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("game-process-helper");
        using var helper = Process.Start(start)!;
        try
        {
            Check(await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "ready", "helper ready");
            _ = helper.Handle; // Retain the kernel object after exit, as the launcher does.
            Check(GameProcessDiscovery.IsRunning(helper, session), "helper alive");
            bool liveBlocked = false, attemptedCreate = false;
            try { GameLaunchGuard.Start(helper.ProcessName, () => attemptedCreate = true); }
            catch (GameAlreadyRunningException ex) { liveBlocked = ex.Conflict.Inspection.Status == GameProcessStatus.Running; }
            Check(liveBlocked && !attemptedCreate, "real live helper blocks creation without relying on UI state");
            using (var next = GameProcessDiscovery.FindRunning(
                [Process.GetProcessById(helper.Id), Process.GetProcessById(current.Id)], session, [helper.Id]))
                Check(next?.Id == current.Id, "previously injected PID excluded without hiding replacement");
            using var deadFirst = Process.GetProcessById(helper.Id);
            using var onlyDead = Process.GetProcessById(helper.Id);
            _ = deadFirst.Handle;
            _ = onlyDead.Handle;
            await helper.StandardInput.WriteLineAsync("exit");
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(!GameProcessDiscovery.IsRunning(helper, session), "exited process with retained handle excluded");

            using var selected = GameProcessDiscovery.FindRunning(
                [deadFirst, Process.GetProcessById(current.Id)], session);
            Check(selected?.Id == current.Id, "dead first candidate does not hide live candidate");
            using var absent = GameProcessDiscovery.FindRunning([onlyDead], session);
            Check(absent is null, "only retained dead records means no running game");
        }
        finally
        {
            if (!helper.HasExited)
            {
                await helper.StandardInput.WriteLineAsync("exit");
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        var disposed = Process.GetCurrentProcess();
        disposed.Dispose();
        Check(!GameProcessDiscovery.IsRunning(disposed, session), "disposed candidate excluded");
        Console.WriteLine($"PASS {checks} game process checks");
        return 0;
    }
}
