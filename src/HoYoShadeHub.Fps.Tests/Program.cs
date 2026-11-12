// 故障注入和受控 Windows helper 测试；不运行游戏，不依赖 WinUI/NuGet 测试框架。
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using HoYoShadeHub.Features.GameLauncher;

if (args.Contains("--helper"))
{
    Console.WriteLine("READY");
    return Console.ReadLine() == "EXIT259" ? 259 : 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("timeout/WAIT_FAILED/thread signal retain payload; no PID stop write", RetainStarted),
    ("partial/failed write, flush/start/open/alloc failures and free retry", InjectionFailures),
    ("attach and local loop pending even after process exit", LocalUsersPending),
    ("fault after thread creation retains real owner", FaultAfterStart),
    ("close failure retries without double free", CloseFailure),
    ("native wait exception retains owner and background retry", WaitException),
    ("background owner rooted; drain blocks until process exits", BackgroundDrain),
    ("concurrent SetTarget/repeated Dispose; single-use Attach", ConcurrentSetDispose),
    ("Dispose during Attach leaves pending native reader intact", ConcurrentAttachDispose),
    ("Dispose during blocked local loop cannot release its resources", BlockedLoop),
    ("maintenance short write transfers owner", MaintenanceWriteFailure),
    ("Attach failure cleans partial initialization without caller Dispose", AttachFailureCleanup),
    ("failed free followed by process exit needs no second remote free", FreeFailureProcessExit),
    ("payload validation rejects before local/remote allocation", PayloadValidation),
    ("constructor slot initialization failure retires allocated slot", SlotInitializationFailure),
    ("Attach initial slot write failure permits Dispose retirement", AttachInitialSlotFailure),
    ("same PID/different process identity rejected before all remote operations", ProcessIdentityMismatch),
    ("Windows helper: real thread timeout/signal; exact process lifetime", WindowsHelper),
    ("Windows helper: process exit with running remote thread", WindowsRunningThreadExit),
};

try
{
    foreach (var test in tests)
    {
        await test.Run();
        await FpsUnlocker.DrainRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(!FpsUnlocker.HasPendingRetirements, "queue was not drained");
        Console.WriteLine($"PASS {test.Name}");
    }
    Console.WriteLine($"PASS {tests.Length}/{tests.Length}; Windows={OperatingSystem.IsWindows()}, arch={RuntimeInformation.ProcessArchitecture}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

static FpsRemoteLifetime Owner(FakeNative native, bool start = true)
{
    var owner = new FpsRemoteLifetime(native);
    Check(owner.Open(123, 77), "fake process identity mismatch");
    if (start) owner.Install(new byte[512], 80);
    return owner;
}

static FpsUnlocker Unlocker(FakeNative native) => new(FpsUnlocker.FallbackShellcode,
    (FakeNative.Pattern, Enumerable.Repeat(true, FakeNative.Pattern.Length).ToArray()), native);

static Task RetainStarted()
{
    var native = new FakeNative();
    var owner = Owner(native);
    lock (owner.Gate)
    {
        owner.CompleteAttach(null);
        native.RejectWrites = true;
        owner.Retire();
        Check(FpsUnlocker.HasPendingRetirements, "owner not registered synchronously");
        Check(!owner.TryRetire(), "timeout released payload");
        native.ThreadWait = FpsRemoteLifetime.Signaled;
        Check(!owner.TryRetire(), "thread signal released payload");
        native.ThreadWait = FpsRemoteLifetime.WaitFailed;
        Check(!owner.TryRetire(), "thread WAIT_FAILED released payload");
        native.ProcessWait = FpsRemoteLifetime.WaitFailed;
        Check(!owner.TryRetire(), "process WAIT_FAILED released payload");
        Check(owner.Remote != IntPtr.Zero && owner.Slot != IntPtr.Zero && native.Closes.Count == 0, "lost live resource");
        Check(native.Writes == 1, "retirement attempted an invalid PID stop write");
        native.ProcessWait = FpsRemoteLifetime.Signaled;
        Check(owner.TryRetire(), "process signal did not retire");
        Check(native.Frees == 0 && native.SlotFrees == 1 && native.Closes.Count == 2, "process-exit cleanup wrong");
    }
    return Task.CompletedTask;
}

static Task InjectionFailures()
{
    foreach (string fault in new[] { "open", "alloc", "write", "short", "flush", "start" })
    {
        var native = new FakeNative { Fault = fault };
        var owner = new FpsRemoteLifetime(native);
        lock (owner.Gate)
        {
            Throws<System.ComponentModel.Win32Exception>(() => { owner.Open(123, 77); owner.Install(new byte[512], 80); });
            Check(native.Starts == (fault == "start" ? 1 : 0), "thread started after failed initialization");
            owner.CompleteAttach(null);
            owner.Retire();
            if (owner.Remote != IntPtr.Zero)
            {
                native.FreeFails = true;
                Check(!owner.TryRetire(), "failed VirtualFreeEx lost owner");
                Check(owner.Remote != IntPtr.Zero && native.SlotFrees == 0 && native.Closes.Count == 0, "failed free released ownership");
                native.FreeFails = false;
            }
            Check(owner.TryRetire(), $"failed cleanup for {fault}");
            Check(native.SlotFrees == 1, "local slot leak");
        }
    }
    return Task.CompletedTask;
}

static Task LocalUsersPending()
{
    var native = new FakeNative();
    var owner = Owner(native);
    var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (owner.Gate)
    {
        owner.Retire();
        native.ProcessWait = FpsRemoteLifetime.Signaled;
        Check(!owner.TryRetire() && native.Waits == 0, "retired before Attach completed");
        owner.CompleteAttach(loop.Task);
        Check(!owner.TryRetire() && native.Waits == 0 && native.SlotFrees == 0, "retired before loop completed");
        loop.SetResult();
        Check(owner.TryRetire(), "pending local user did not retire after completion");
    }
    return Task.CompletedTask;
}

static Task FaultAfterStart()
{
    var native = new FakeNative();
    var owner = Owner(native, false);
    lock (owner.Gate)
    {
        try { owner.Install(new byte[512], 80); throw new ApplicationException("after CreateRemoteThread"); }
        catch (ApplicationException) { owner.CompleteAttach(null); owner.Retire(); }
        Check(!owner.TryRetire() && native.Starts == 1 && native.Closes.Count == 0, "lost owner after start fault");
        native.ProcessWait = FpsRemoteLifetime.Signaled;
        Check(owner.TryRetire(), "start fault cleanup");
    }
    return Task.CompletedTask;
}

static Task CloseFailure()
{
    var native = new FakeNative { CloseFails = true };
    var owner = Owner(native);
    lock (owner.Gate)
    {
        native.ProcessWait = FpsRemoteLifetime.Signaled;
        owner.CompleteAttach(null);
        owner.Retire();
        Check(!owner.TryRetire() && owner.Process != IntPtr.Zero && native.SlotFrees == 0, "close failure lost owner");
        native.CloseFails = false;
        Check(owner.TryRetire() && owner.TryRetire(), "close retry failed");
        Check(native.SlotFrees == 1 && native.Closes.Count == 2, "double cleanup");
    }
    return Task.CompletedTask;
}

static async Task WaitException()
{
    var native = new FakeNative { WaitThrows = true };
    var owner = Owner(native);
    lock (owner.Gate)
    {
        owner.CompleteAttach(null);
        owner.Retire();
        Throws<ApplicationException>(() => owner.TryRetire());
        Check(owner.Remote != IntPtr.Zero && owner.Slot != IntPtr.Zero, "exception lost owner");
    }
    await Task.Delay(600);
    Check(FpsUnlocker.HasPendingRetirements && native.SlotFrees == 0, "pump exception lost owner");
    native.WaitThrows = false;
    native.ProcessWait = FpsRemoteLifetime.Signaled;
}

static async Task BackgroundDrain()
{
    var native = new FakeNative();
    WeakReference weak;
    {
        var owner = Owner(native);
        owner.CompleteAttach(null);
        owner.Retire();
        weak = new WeakReference(owner);
    }
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Check(weak.IsAlive, "background retirement failed to root owner");
    Task drain = FpsUnlocker.DrainRetirementsAsync();
    await Task.Delay(80);
    Check(!drain.IsCompleted && native.SlotFrees == 0, "drain finished while target alive");
    native.ProcessWait = FpsRemoteLifetime.Signaled;
    await drain.WaitAsync(TimeSpan.FromSeconds(5));
    Check(native.SlotFrees == 1, "background retirement failed");
}

static async Task ConcurrentSetDispose()
{
    var native = new FakeNative();
    var unlocker = Unlocker(native);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
    {
        for (int n = 0; n < 500; n++)
        {
            if ((n + i) % 11 == 0) unlocker.Dispose();
            try { unlocker.SetTarget(n); } catch (ObjectDisposedException) { }
        }
    })));
    await FpsUnlocker.DrainRetirementsAsync();
    Check(native.SlotFrees == 1 && native.BadSlotAccesses == 0, "SetTarget used freed slot");
    using var self = Process.GetCurrentProcess();
    try { await unlocker.AttachAsync(self, 120, TimeSpan.FromSeconds(2)); throw new Exception("Attach accepted after Dispose"); }
    catch (ObjectDisposedException) { }
}

static async Task ConcurrentAttachDispose()
{
    var native = new FakeNative { BlockHeader = true };
    var unlocker = Unlocker(native);
    using var self = Process.GetCurrentProcess();
    Task<bool> attach = Task.Run(() => unlocker.AttachAsync(self, 120, TimeSpan.FromSeconds(2)));
    Check(native.ReadEntered.Wait(5000), "Attach never entered header read");
    try
    {
        try { await unlocker.AttachAsync(self, 120, TimeSpan.FromSeconds(2)); throw new Exception("second Attach accepted"); }
        catch (InvalidOperationException) { }
        unlocker.Dispose();
        await Task.Delay(80);
        Check(!attach.IsCompleted && native.SlotFrees == 0 && native.Closes.Count == 0, "Dispose freed active Attach");
    }
    finally { native.ReadRelease.Set(); }
    Check(!await attach.WaitAsync(TimeSpan.FromSeconds(5)), "canceled Attach armed a thread");
    Check(native.Starts == 0, "thread started after Dispose");
}

static async Task BlockedLoop()
{
    var native = new FakeNative { BlockLoopWait = true };
    var unlocker = Unlocker(native);
    using var self = Process.GetCurrentProcess();
    Check(await unlocker.AttachAsync(self, 120, TimeSpan.FromSeconds(2)), "fake attach failed");
    Check(native.LoopEntered.Wait(5000), "loop did not enter native wait");
    unlocker.Dispose();
    native.ProcessWait = FpsRemoteLifetime.Signaled;
    try
    {
        await Task.Delay(80);
        Check(native.SlotFrees == 0 && native.Closes.Count == 0, "freed blocked local loop");
    }
    finally { native.LoopRelease.Set(); }
}

static async Task MaintenanceWriteFailure()
{
    var native = new FakeNative { ShortMaintenance = true };
    using var self = Process.GetCurrentProcess();
    using var unlocker = Unlocker(native);
    Check(await unlocker.AttachAsync(self, 120, TimeSpan.FromSeconds(2)), "fake attach failed");
    Check(await Task.Run(() => native.MaintenanceEntered.Wait(5000)), "maintenance write not reached");
    var deadline = Stopwatch.StartNew();
    while (!FpsUnlocker.HasPendingRetirements && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
    Check(FpsUnlocker.HasPendingRetirements && native.SlotFrees == 0, "write failure did not transfer owner");
    native.ProcessWait = FpsRemoteLifetime.Signaled;
}

static async Task AttachFailureCleanup()
{
    using var self = Process.GetCurrentProcess();
    foreach (string fault in new[] { "open", "alloc", "write", "short", "flush", "start", "read", "shortread" })
    {
        var native = new FakeNative { Fault = fault };
        var unlocker = Unlocker(native);
        Check(!await unlocker.AttachAsync(self, 120, TimeSpan.FromSeconds(2)), $"Attach accepted fault {fault}");
        Check(!string.IsNullOrWhiteSpace(unlocker.LastError), "missing Attach failure reason");
        await FpsUnlocker.DrainRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(native.SlotFrees == 1, $"failed Attach leaked slot for {fault}");
        Check(native.Starts == (fault == "start" ? 1 : 0), "Attach started after initialization failure");
        unlocker.Dispose();
        Throws<ObjectDisposedException>(() => unlocker.SetTarget(120));
    }
}

static Task FreeFailureProcessExit()
{
    var native = new FakeNative { Fault = "start", FreeFails = true };
    var owner = Owner(native, false);
    lock (owner.Gate)
    {
        Throws<System.ComponentModel.Win32Exception>(() => owner.Install(new byte[512], 80));
        owner.CompleteAttach(null);
        owner.Retire();
        Check(!owner.TryRetire() && native.Frees == 1 && native.SlotFrees == 0, "failed free lost owner");
        native.ProcessWait = FpsRemoteLifetime.Signaled;
        Check(owner.TryRetire() && native.Frees == 1 && native.SlotFrees == 1, "free was retried on dead process");
    }
    return Task.CompletedTask;
}

static Task PayloadValidation()
{
    var native = new FakeNative();
    Throws<ArgumentException>(() => new FpsUnlocker(new byte[4097], (FakeNative.Pattern, new bool[10]), native));
    Throws<ArgumentException>(() => new FpsUnlocker(new byte[3], (FakeNative.Pattern, new bool[10]), native));
    Throws<ArgumentException>(() => new FpsUnlocker(FpsUnlocker.FallbackShellcode, (new byte[1], new bool[1]), native));
    Check(native.SlotAllocations == 0, "invalid constructor allocated slot");
    var owner = new FpsRemoteLifetime(native);
    lock (owner.Gate)
    {
        Throws<ArgumentException>(() => owner.Install(new byte[4097], 0));
        Throws<ArgumentException>(() => owner.Install(new byte[512], 512));
        owner.CompleteAttach(null);
        owner.Retire();
        Check(owner.TryRetire() && native.RemoteAllocations == 0, "invalid payload allocated remote memory");
    }
    return Task.CompletedTask;
}

static async Task SlotInitializationFailure()
{
    var native = new FakeNative { SlotWriteThrows = true, SlotFreeThrows = true };
    Throws<ApplicationException>(() => new FpsRemoteLifetime(native));
    await Task.Delay(80);
    Check(FpsUnlocker.HasPendingRetirements && native.SlotAllocations == 1 && native.SlotFrees == 0,
        "constructor failure lost allocated slot owner");
    native.SlotFreeThrows = false;
    await FpsUnlocker.DrainRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(native.SlotFrees == 1, "constructor rollback did not free slot");
}

static async Task AttachInitialSlotFailure()
{
    var native = new FakeNative();
    var unlocker = Unlocker(native);
    native.SlotWriteThrows = true;
    using var self = Process.GetCurrentProcess();
    try
    {
        await unlocker.AttachAsync(self, 120, TimeSpan.FromSeconds(2));
        throw new InvalidOperationException("initial Attach slot write accepted fault");
    }
    catch (ApplicationException) { }
    unlocker.Dispose();
    await FpsUnlocker.DrainRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(native.SlotFrees == 1 && native.Starts == 0, "initial slot write left Attach permanently pending");
}

static async Task ProcessIdentityMismatch()
{
    var native = new FakeNative { IdentityMatches = false };
    var unlocker = Unlocker(native);
    using var self = Process.GetCurrentProcess();
    Check(!await unlocker.AttachAsync(self, 120, TimeSpan.FromSeconds(2)), "different process identity accepted");
    Check(native.OpenedPid == self.Id && native.OriginalProcess != IntPtr.Zero && native.IdentityChecks == 1,
        "identity was not checked against fixed original handle for same PID");
    Check(native.Reads == 0 && native.RemoteAllocations == 0 && native.Writes == 0 && native.Starts == 0,
        "remote operation occurred before identity validation");
    Check(unlocker.LastError?.Contains("身份") == true, "identity rejection not reported");
    await FpsUnlocker.DrainRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(native.SlotFrees == 1 && native.Closes.SequenceEqual(new IntPtr[] { 10 }), "mismatched opened handle leaked");
}

static async Task WindowsHelper() => await RealHelper(false);
static async Task WindowsRunningThreadExit() => await RealHelper(true);

static async Task RealHelper(bool exitWhileRunning)
{
    Check(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "requires Windows x64");
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
    };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--helper");
    using var helper = Process.Start(start)!;
    var native = new RecordingNative();
    var owner = new FpsRemoteLifetime(native);
    try
    {
        Check(await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "READY", "helper handshake failed");
        Check(owner.Open(helper.Id, helper.SafeHandle.DangerousGetHandle()), "actual helper process identity mismatch");
        using (var self = Process.GetCurrentProcess())
            Check(!native.IsSameProcess(self.SafeHandle.DangerousGetHandle(), owner.Process), "actual different processes compared equal");
        // cmp dword ptr [rip+0xF9],0; je start; xor eax,eax; ret.
        // helper thread 只等待本分配 +0x100 的开关，不执行游戏 payload。
        byte[] payload = new byte[0x104];
        new byte[] { 0x83, 0x3D, 0xF9, 0, 0, 0, 0, 0x74, 0xF7, 0x31, 0xC0, 0xC3 }.CopyTo(payload, 0);
        owner.Install(payload, 0);
        Check(native.Wait(native.Thread) == FpsRemoteLifetime.Timeout, "actual thread did not stay pending");
        if (!exitWhileRunning)
        {
            Check(owner.Write(owner.Remote + 0x100, BitConverter.GetBytes(1)), "helper release write failed");
            var deadline = Stopwatch.StartNew();
            while (native.Wait(native.Thread) != FpsRemoteLifetime.Signaled && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Check(native.Wait(native.Thread) == FpsRemoteLifetime.Signaled, "actual thread never signaled");
        }
        owner.CompleteAttach(null);
        owner.Retire();
        Task drain = FpsUnlocker.DrainRetirementsAsync();
        await Task.Delay(80);
        Check(!drain.IsCompleted && owner.Slot != IntPtr.Zero && owner.Remote != IntPtr.Zero && native.Closes == 0,
            "thread signal/timeout released live process resources");
        await helper.StandardInput.WriteLineAsync(exitWhileRunning ? "EXIT259" : "EXIT");
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(helper.ExitCode == (exitWhileRunning ? 259 : 0), "helper exit code mismatch");
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Check(native.Frees == 0 && native.SlotFrees == 1 && native.Closes == 2, "actual process cleanup failed");
        Check(native.Wait(IntPtr.Zero) == FpsRemoteLifetime.WaitFailed, "actual invalid handle did not fail wait");
    }
    finally
    {
        if (!helper.HasExited) { await helper.StandardInput.WriteLineAsync("EXIT"); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        owner.CompleteAttach(null);
        owner.Retire();
    }
}

sealed class FakeNative : IFpsRemoteNative
{
    internal static readonly byte[] Pattern = [0x8B, 0x0D, 0, 0, 0, 0, 0xEB, 0, 0x33, 0xC0];
    internal string Fault = "";
    internal volatile uint ProcessWait = FpsRemoteLifetime.Timeout;
    internal uint ThreadWait = FpsRemoteLifetime.Timeout;
    internal bool FreeFails, CloseFails, RejectWrites, BlockHeader, BlockLoopWait, ShortMaintenance;
    internal bool IdentityMatches = true;
    internal IntPtr OriginalProcess;
    internal int IdentityChecks, OpenedPid, Reads;
    internal volatile bool WaitThrows;
    internal volatile bool SlotWriteThrows, SlotFreeThrows;
    internal int Frees, SlotFrees, Writes, Starts, Waits, BadSlotAccesses, SlotAllocations, RemoteAllocations;
    internal readonly List<IntPtr> Closes = new();
    internal readonly ManualResetEventSlim ReadEntered = new(), ReadRelease = new(), LoopEntered = new(), LoopRelease = new(), MaintenanceEntered = new();
    private readonly HashSet<IntPtr> _slots = new();
    public int LastError => 5;
    public IntPtr Open(int pid) { OpenedPid = pid; return Fault == "open" ? IntPtr.Zero : 10; }
    public bool IsSameProcess(IntPtr originalProcess, IntPtr openedProcess)
    {
        OriginalProcess = originalProcess;
        IdentityChecks++;
        return IdentityMatches;
    }
    public IntPtr Allocate(IntPtr process) { RemoteAllocations++; return Fault == "alloc" ? IntPtr.Zero : 1000; }
    public bool Free(IntPtr process, IntPtr remote) { Frees++; return !FreeFails; }
    public bool Read(IntPtr process, IntPtr address, byte[] buffer, out nuint read)
    {
        Interlocked.Increment(ref Reads);
        read = (nuint)buffer.Length;
        if (Fault == "read") { read = 0; return false; }
        if (Fault == "shortread") { read--; return true; }
        if (buffer.Length == 4096)
        {
            if (BlockHeader) { ReadEntered.Set(); if (!ReadRelease.Wait(10000)) throw new TimeoutException("header gate"); }
            BitConverter.GetBytes(128).CopyTo(buffer, 0x3C);
            BitConverter.GetBytes(0x00004550).CopyTo(buffer, 128);
            BitConverter.GetBytes((ushort)1).CopyTo(buffer, 134);
            System.Text.Encoding.ASCII.GetBytes(".text").CopyTo(buffer, 392);
            BitConverter.GetBytes(32).CopyTo(buffer, 400);
            BitConverter.GetBytes(4096).CopyTo(buffer, 404);
        }
        else if (buffer.Length == 32) Pattern.CopyTo(buffer, 0);
        else if (buffer.Length == 4) BitConverter.GetBytes(60).CopyTo(buffer, 0);
        return true;
    }
    public bool Write(IntPtr process, IntPtr address, byte[] buffer, out nuint written)
    {
        Writes++;
        written = (nuint)buffer.Length;
        if (RejectWrites || Fault == "write") { written = 0; return false; }
        if (Fault == "short" || (ShortMaintenance && buffer.Length == 4))
        {
            written--;
            if (buffer.Length == 4) MaintenanceEntered.Set();
        }
        return true;
    }
    public bool Flush(IntPtr process, IntPtr address, nuint size) => Fault != "flush";
    public IntPtr StartThread(IntPtr process, IntPtr entry) { Starts++; return Fault == "start" ? 0 : 20; }
    public uint Wait(IntPtr handle)
    {
        Interlocked.Increment(ref Waits);
        if (WaitThrows) throw new ApplicationException("injected native wait failure");
        if (BlockLoopWait && handle == 10)
        {
            LoopEntered.Set();
            if (!LoopRelease.Wait(10000)) throw new TimeoutException("loop gate");
        }
        return handle == 10 ? ProcessWait : ThreadWait;
    }
    public bool Close(IntPtr handle)
    {
        if (CloseFails) return false;
        if (Closes.Contains(handle)) throw new InvalidOperationException("double close");
        Closes.Add(handle);
        return true;
    }
    public IntPtr AllocateSlot() { SlotAllocations++; var slot = Marshal.AllocHGlobal(4); lock (_slots) _slots.Add(slot); return slot; }
    public void WriteSlot(IntPtr slot, int value) { lock (_slots) { CheckSlot(slot); if (SlotWriteThrows) throw new ApplicationException("slot write fault"); Marshal.WriteInt32(slot, value); } }
    public int ReadSlot(IntPtr slot) { lock (_slots) { CheckSlot(slot); return Marshal.ReadInt32(slot); } }
    private void CheckSlot(IntPtr slot)
    {
        if (!_slots.Contains(slot)) { BadSlotAccesses++; throw new InvalidOperationException("freed slot access"); }
    }
    public void FreeSlot(IntPtr slot) { lock (_slots) { CheckSlot(slot); if (SlotFreeThrows) throw new ApplicationException("slot free fault"); SlotFrees++; _slots.Remove(slot); Marshal.FreeHGlobal(slot); } }
}

sealed class RecordingNative : IFpsRemoteNative
{
    private readonly FpsRemoteNative _native = new();
    internal IntPtr Thread;
    internal int Frees, Closes, SlotFrees;
    public int LastError => _native.LastError;
    public IntPtr Open(int pid) => _native.Open(pid);
    public bool IsSameProcess(IntPtr originalProcess, IntPtr openedProcess) => _native.IsSameProcess(originalProcess, openedProcess);
    public IntPtr Allocate(IntPtr process) => _native.Allocate(process);
    public bool Free(IntPtr process, IntPtr remote) { Frees++; return _native.Free(process, remote); }
    public bool Read(IntPtr process, IntPtr address, byte[] buffer, out nuint read) => _native.Read(process, address, buffer, out read);
    public bool Write(IntPtr process, IntPtr address, byte[] buffer, out nuint written) => _native.Write(process, address, buffer, out written);
    public bool Flush(IntPtr process, IntPtr address, nuint size) => _native.Flush(process, address, size);
    public IntPtr StartThread(IntPtr process, IntPtr entry) => Thread = _native.StartThread(process, entry);
    public uint Wait(IntPtr handle) => _native.Wait(handle);
    public bool Close(IntPtr handle) { Closes++; return _native.Close(handle); }
    public IntPtr AllocateSlot() => _native.AllocateSlot();
    public void WriteSlot(IntPtr slot, int value) => _native.WriteSlot(slot, value);
    public int ReadSlot(IntPtr slot) => _native.ReadSlot(slot);
    public void FreeSlot(IntPtr slot) { SlotFrees++; _native.FreeSlot(slot); }
}
