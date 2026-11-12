// Controlled session gates reproduce CLI exit/readiness races without opening a game or injecting code.
using HoYoShadeHub.Features.GameLauncher;
using System.Diagnostics;
using System.Reflection;

ProcessStartInfo StartInfo(string verb)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add(verb);
    return info;
}

if (args is ["lifetime-target-worker"])
{
    Console.WriteLine("target-ready");
    return await Console.In.ReadLineAsync() == "exit" ? 0 : 1;
}
if (args is ["session-owner-worker"])
{
    using var target = Process.Start(StartInfo("lifetime-target-worker"))!;
    if (await target.StandardOutput.ReadLineAsync() != "target-ready") return 1;
    var sessions = new FpsUnlockSessionRegistry();
    var session = sessions.GetOrStart($"{target.Id}:{target.StartTime.ToUniversalTime().Ticks}", async owner =>
    {
        owner.ReportAttached(true, "controlled child attached");
        await target.WaitForExitAsync();
    });
    if (!(await session.Ready).Armed) return 1;
    Task stopRequest = Task.Run(async () =>
    {
        if (await Console.In.ReadLineAsync() == "exit")
        {
            await target.StandardInput.WriteLineAsync("exit");
            await target.StandardInput.FlushAsync();
        }
    });
    Console.WriteLine("batch-complete-owner-retained");
    await sessions.DrainAsync();
    await stopRequest;
    Console.WriteLine("exact-target-exited-owner-ended");
    return target.ExitCode;
}

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}
TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
Task Timeout(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

var registry = new FpsUnlockSessionRegistry();
var start = Gate();
var exit = Gate();
int creates = 0;
var first = registry.GetOrStart("42:100", async session =>
{
    Interlocked.Increment(ref creates);
    await start.Task;
    session.ReportAttached(true, "armed");
    await exit.Task;
});
Check(registry.HasActiveSessions, "Ownership must be visible before attach starts");
Check(!first.Ready.IsCompleted, "Launch cannot claim success before attach result");
var same = registry.GetOrStart("42:100", _ => throw new Exception("Duplicate session factory executed"));
Check(ReferenceEquals(first, same), "Same exact process identity shares one owner");
start.SetResult();
Check((await first.Ready).Armed && creates == 1, "Attach result reaches launch exactly once");
Task drain = registry.DrainAsync();
Check(!drain.IsCompleted && !first.Completion.IsCompleted, "Batch completion must retain armed owner before process exit");
// A batch wait step can complete while the session is still alive; lifetime is not awaited in launch itself.
await Task.Yield();
Check(!drain.IsCompleted, "Advancing batch work must not retire the FPS session");
exit.SetResult();
await Timeout(drain);
Check(!registry.HasActiveSessions, "Exact process exit permits normal CLI return");

var newerExit = Gate();
var newer = registry.GetOrStart("42:200", async session => { session.ReportAttached(true, "new-instance"); await newerExit.Task; });
Check((await newer.Ready).Armed, "A reused PID with different start identity is independent");
Task newerDrain = registry.DrainAsync();
Check(!newerDrain.IsCompleted, "Prior process exit cannot end a later instance");
newerExit.SetResult();
await Timeout(newerDrain);

var rejected = registry.GetOrStart("50:100", session => { session.ReportAttached(false, "OpenProcess denied"); return Task.CompletedTask; });
Check(!(await rejected.Ready).Armed, "Attachment failure is explicitly reported");
await Timeout(registry.DrainAsync());
Check(!registry.HasActiveSessions, "Failure without live ownership does not keep CLI alive");

var missing = registry.GetOrStart("51:100", _ => Task.CompletedTask);
Check(!(await missing.Ready).Armed, "Returning before readiness must fail closed");
await Timeout(registry.DrainAsync());

var failed = registry.GetOrStart("52:100", _ => throw new IOException("attach failed"));
Check(!(await failed.Ready).Armed, "Unexpected attach exception unblocks readiness with failure");
try { await Timeout(registry.DrainAsync()); throw new Exception("Expected failed session"); }
catch (IOException) { checks++; }
Check(!registry.HasActiveSessions, "Faulted session is observed and removed");

var faultGate = Gate();
var monitorFailed = registry.GetOrStart("53:100", async session => { session.ReportAttached(true, "armed"); await faultGate.Task; throw new IOException("monitor failed"); });
Check((await monitorFailed.Ready).Armed, "Monitor can fail after successful readiness");
Task faultDrain = registry.DrainAsync();
faultGate.SetResult();
try { await Timeout(faultDrain); throw new Exception("Expected failed monitor"); }
catch (IOException) { checks++; }

var oneExit = Gate();
var twoExit = Gate();
var one = registry.GetOrStart("60:1", async session => { session.ReportAttached(true, "one"); await oneExit.Task; });
await one.Ready;
Task twoDrain = registry.DrainAsync();
var two = registry.GetOrStart("61:1", async session => { session.ReportAttached(true, "two"); await twoExit.Task; });
await two.Ready;
oneExit.SetResult();
await Timeout(one.Completion);
Check(!twoDrain.IsCompleted, "Drain must include ownership registered while draining");
twoExit.SetResult();
await Timeout(twoDrain);
Check(!registry.HasActiveSessions, "All exact-target sessions retired");

var failedExit = Gate();
var lateExit = Gate();
var failedDuringDrain = registry.GetOrStart("70:1", async session =>
{
    session.ReportAttached(true, "first");
    await failedExit.Task;
    throw new IOException("earlier monitor failed");
});
await failedDuringDrain.Ready;
Task failedDrain = registry.DrainAsync();
var late = registry.GetOrStart("71:1", async session => { session.ReportAttached(true, "late"); await lateExit.Task; });
await late.Ready;
failedExit.SetResult();
try { await Timeout(failedDuringDrain.Completion); }
catch (IOException) { checks++; }
Check(!failedDrain.IsCompleted, "A failed snapshot must still drain ownership registered later");
lateExit.SetResult();
try { await Timeout(failedDrain); throw new Exception("Expected original monitor failure after all owners end"); }
catch (IOException error) { Check(error.Message == "earlier monitor failed", "Drain reports the original error after retiring later ownership"); }
Check(!registry.HasActiveSessions, "Failed drain leaves no active later owner");
using (var ownerProcess = Process.Start(StartInfo("session-owner-worker"))!)
{
    try
    {
        string? ready = await ownerProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(ready == "batch-complete-owner-retained", "Real owner reaches end of batch");
        Check(!ownerProcess.HasExited, "Real CLI owner remains alive while its exact target process runs");
    }
    finally
    {
        await ownerProcess.StandardInput.WriteLineAsync("exit");
        await ownerProcess.StandardInput.FlushAsync();
    }
    await ownerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(ownerProcess.ExitCode == 0, "Natural target exit allows owner process to return successfully");
    Check((await ownerProcess.StandardOutput.ReadToEndAsync()).Contains("exact-target-exited-owner-ended"), "Owner observes exact target exit before returning");
    Check(string.IsNullOrWhiteSpace(await ownerProcess.StandardError.ReadToEndAsync()), "Controlled process integration has no errors");
}
Console.WriteLine($"FPS session checks: PASS {checks}; controlled tasks and helper processes; gameTested=false; uiTested=false.");
return 0;
