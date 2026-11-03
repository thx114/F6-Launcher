using System.ComponentModel;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class ExactProcessStopTests
{
    private sealed class Fake : IExactStopProcess
    {
        public int Pid { get; set; } = 100;
        public long CreationFileTime { get; set; } = 133000000000000001;
        public string ImagePath { get; set; } = @"D:\Games\YuanShen.exe";
        public bool HasExited { get; set; }
        public bool ExitOnClose, DenyTerminate, Disposed;
        public int Closes, Kills;
        public void RequestClose() { Closes++; if (ExitOnClose) HasExited = true; }
        public void Terminate() { Kills++; if (DenyTerminate) throw new Win32Exception(5); HasExited = true; }
        public void Dispose() => Disposed = true;
    }

    public static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
        var expected = new ExactStopCondition(100, 133000000000000001);
        string[] valid = ["stopgame", "--biz", "hk4e_cn", "--expected-pid", "100", "--expected-creation-filetime", "133000000000000001"];
        Check(ExactStopCondition.Parse(["stopgame", "--biz", "hk4e_cn"]) is null, "legacy stop keeps legacy dispatch");
        Check(ExactStopCondition.Parse(valid) == expected, "FILETIME exact decimal preserves last 100ns digit");
        foreach (string[] invalid in new string[][] {
            ["--expected-pid", "100"], ["--expected-creation-filetime", "133000000000000001"],
            ["--expected-pid"], ["--expected-pid", "-1", "--expected-creation-filetime", "1"],
            ["--expected-pid", "0", "--expected-creation-filetime", "1"],
            ["--expected-pid", "2147483648", "--expected-creation-filetime", "1"],
            ["--expected-pid", "abc", "--expected-creation-filetime", "1"],
            ["--expected-pid", "100", "--expected-creation-filetime", "1.2"],
            ["--expected-pid", "100", "--expected-creation-filetime", "9223372036854775808"],
            ["--expected-pid", "100", "--expected-creation-filetime", "9223372036854775807"],
            ["--expected-pid", "100", "--expected-creation-filetime", "-1"],
            ["--expected-pid", "100", "--expected-creation-filetime", "0"],
            ["--expected-pid", "100", "--expected-pid", "101", "--expected-creation-filetime", "1"],
            ["--expected-pid=100", "--expected-creation-filetime", "1"] })
        {
            bool denied = false; try { ExactStopCondition.Parse(invalid); } catch (ArgumentException) { denied = true; }
            Check(denied, "invalid or incomplete exact options reject: " + string.Join(' ', invalid));
        }
        async Task Denied(Fake process, string name)
        {
            bool denied = false;
            try { await ExactProcessStop.StopAsync(expected, @"D:\Games\YuanShen.exe", _ => process, () => Task.CompletedTask); }
            catch { denied = true; }
            Check(denied && process.Closes == 0 && process.Kills == 0 && process.Disposed, name);
        }
        await Denied(new Fake { CreationFileTime = expected.CreationFileTime + 1 }, "PID reuse with different creation identity rejects without side effects");
        await Denied(new Fake { Pid = 101 }, "wrong bound PID rejects");
        await Denied(new Fake { ImagePath = @"D:\OtherGame\YuanShen.exe" }, "same executable name at different game path rejects");
        await Denied(new Fake { HasExited = true }, "already exited expected process rejects");
        bool accessDenied = false;
        try { await ExactProcessStop.StopAsync(expected, @"D:\Games\YuanShen.exe", _ => throw new Win32Exception(5)); }
        catch (Win32Exception ex) { accessDenied = ex.NativeErrorCode == 5; }
        Check(accessDenied, "access denied is propagated without alternate lookup");
        bool gone = false;
        try { await ExactProcessStop.StopAsync(expected, @"D:\Games\YuanShen.exe", _ => throw new Win32Exception(87)); }
        catch (Win32Exception) { gone = true; }
        Check(gone, "missing PID rejects without alternate lookup");
        var graceful = new Fake { ExitOnClose = true }; int binds = 0;
        await ExactProcessStop.StopAsync(expected, @"D:\Games\YuanShen.exe", _ => { binds++; return graceful; }, () => Task.CompletedTask);
        Check(binds == 1 && graceful.Closes == 1 && graceful.Kills == 0 && graceful.Disposed, "graceful exit uses one binding");
        var original = new Fake(); var replacement = new Fake { CreationFileTime = expected.CreationFileTime + 10 }; binds = 0;
        await ExactProcessStop.StopAsync(expected, original.ImagePath, _ => { binds++; return binds == 1 ? original : replacement; },
            () => { original.HasExited = true; return Task.CompletedTask; });
        Check(binds == 1 && original.Kills == 0 && replacement.Closes == 0 && replacement.Kills == 0,
            "expected game exits and new game starts during wait: replacement never touched");
        var forced = new Fake(); binds = 0;
        await ExactProcessStop.StopAsync(expected, forced.ImagePath, _ => { binds++; return forced; }, () => Task.CompletedTask);
        Check(binds == 1 && forced.Closes == 1 && forced.Kills == 1 && forced.Disposed, "forced branch uses same verified bound process");
        var deniedKill = new Fake { DenyTerminate = true }; binds = 0; accessDenied = false;
        try { await ExactProcessStop.StopAsync(expected, deniedKill.ImagePath, _ => { binds++; return deniedKill; }, () => Task.CompletedTask); }
        catch (Win32Exception) { accessDenied = true; }
        Check(accessDenied && binds == 1 && deniedKill.Disposed, "terminate denial never falls back to another process");
        bool badGame = false;
        try { await ExactProcessStop.StopAsync(expected, "", _ => throw new Exception("must not bind")); }
        catch (InvalidOperationException) { badGame = true; }
        Check(badGame, "unverifiable game identity rejects before binding");
        // 只绑定自测宿主，故意使用错误创建时间；在发送任何关闭请求前拒绝。
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        bool nativeRejected = false;
        try { await ExactProcessStop.StopAsync(new(self.Id, self.StartTime.ToFileTimeUtc() + 1), Environment.ProcessPath!); }
        catch (InvalidOperationException) { nativeRejected = true; }
        Check(nativeRejected, "native handle identity mismatch rejects self-test host without sending close or terminate");
        Console.WriteLine($"Exact stop tests: PASS {passed}, FAIL 0 (fake processes plus native self-host rejection; no game processes)");
        return 0;
    }
}
