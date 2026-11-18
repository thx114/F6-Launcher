using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HoYoShadeHub.Features.GameLauncher;

namespace F6Launcher.Background;

internal static class Program
{
    private static readonly ConcurrentDictionary<int, Watch> Games = new();
    private static readonly ConcurrentDictionary<int, FpsUnlocker> Fps = new();
    private static readonly CancellationTokenSource Stop = new();
    private static string _pipe = string.Empty, _gui = string.Empty, _data = string.Empty;
    private static int _ownerPid;
    private static long _ownerStart;
    private static volatile bool _parked;
    private static int _restoring;
    private static bool _fixture;
    private static readonly object LogGate = new();

    [STAThread]
    private static int Main(string[] args)
    {
        _fixture = args.Contains("--fixture");
        _pipe = Value(args, "--pipe");
        if (string.IsNullOrWhiteSpace(_pipe)) return 2;
        using var singleton = new Mutex(true, "Local\\" + _pipe, out bool first);
        if (!first) return 0;
        _ = Task.Run(ServeAsync);
        _ = Task.Run(WatchAsync);
        NativeTray.Run(Open, () => Stop.Cancel(), _fixture);
        Stop.Cancel();
        foreach (var fps in Fps.Values) fps.Dispose();
        // FPS payload and slot lifetimes must outlive every remote reader.
        FpsRemoteLifetime.DrainRetirementsAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static string Value(string[] args, string name)
    { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : string.Empty; }

    private static async Task ServeAsync()
    {
        while (!Stop.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new(_pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(Stop.Token);
                var connected = server;
                server = null;
                _ = HandleAsync(connected);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log("pipe: " + ex.Message); await Task.Delay(500); }
            finally { server?.Dispose(); }
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(Stop.Token);
                var request = line is null ? null : JsonSerializer.Deserialize(line, BackgroundJson.Default.BackgroundRequest);
                BackgroundReply reply;
                try { reply = request is null ? new(false, "Missing request") : await DispatchAsync(request); }
                catch (Exception ex) { Log(ex.ToString()); reply = new(false, ex.Message); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, BackgroundJson.Default.BackgroundReply));
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<BackgroundReply> DispatchAsync(BackgroundRequest r)
    {
        switch (r.Command)
        {
            case "hello":
                if (!Matches(r.OwnerPid, r.OwnerStartTicks)) return new(false, "GUI identity expired");
                bool replacingOwner = _ownerPid != 0 && _ownerPid != r.OwnerPid;
                if (replacingOwner)
                    foreach (var owned in Games.Values) owned.Transferred = true;
                _ownerPid = r.OwnerPid; _ownerStart = r.OwnerStartTicks;
                _gui = r.GuiPath ?? _gui; _data = r.UserData ?? _data;
                _parked = false; Interlocked.Exchange(ref _restoring, 0);
                NativeTray.SetVisible(false);
                return Memory() with { Game = Games.Values.FirstOrDefault()?.Plan, Games = Games.Values.Select(w => w.Plan).ToArray() };
            case "status": return Memory();
            case "track":
            case "transfer":
                if (r.Game is not { } game) return new(false, "Missing game");
                if (Games.TryGetValue(game.Pid, out var existing) && existing.Plan.StartTicks == game.StartTicks)
                {
                    if (r.Command == "transfer") existing.Transferred = true;
                    if (game.SessionDirectory is not null || existing.Plan.SessionDirectory is null)
                        existing.Plan = game with { RecordPlayTime = existing.Plan.RecordPlayTime || game.RecordPlayTime,
                            DatabasePath = game.DatabasePath ?? existing.Plan.DatabasePath };
                    else if (game.RecordPlayTime) existing.Plan = existing.Plan with { RecordPlayTime = true, DatabasePath = game.DatabasePath };
                    return new(true, Game: existing.Plan);
                }
                var process = Process.GetProcessById(game.Pid);
                if (process.StartTime.ToUniversalTime().Ticks != game.StartTicks || process.HasExited)
                { process.Dispose(); return new(false, "Game identity expired"); }
                _ = process.SafeHandle; // Retain this kernel process object; never fall back to a recycled PID.
                Games[game.Pid] = new(process, game) { Transferred = r.Command == "transfer" };
                return new(true, Game: game);
            case "lookup":
                return r.Game is { } lookup && Games.TryGetValue(lookup.Pid, out var watch)
                    && watch.Plan.StartTicks == lookup.StartTicks ? new(true, Game: watch.Plan) : new(false);
            case "park":
                if (r.OwnerPid != _ownerPid || r.OwnerStartTicks != _ownerStart || !Games.Values.Any(w => !w.Process.HasExited))
                    return new(false, "No live game to watch");
                _parked = true;
                Log("GUI unloading; background host owns game watches");
                return Memory();
            case "attach-fps":
                if (r.Fps is not { } attach || !Matches(attach.Pid, attach.StartTicks)) return new(false, "FPS target expired");
                if (MultiplayerGameGuard.IsActive) return new(false, MultiplayerGameGuard.BlockReason());
                if (Fps.TryRemove(attach.Pid, out var previous)) previous.Dispose();
                using (var target = Process.GetProcessById(attach.Pid))
                {
                    var unlocker = new FpsUnlocker(attach.Shellcode, (attach.Pattern, attach.Mask));
                    bool attached = await unlocker.AttachAsync(target, attach.Target, TimeSpan.FromSeconds(attach.TimeoutSeconds));
                    if (attached) Fps[attach.Pid] = unlocker;
                    else unlocker.Dispose();
                    return new(attached, unlocker.LastError, SkippedGameExited: unlocker.SkippedGameExited, PatternNotFound: unlocker.PatternNotFound);
                }
            case "stop-fps":
                if (r.Game is { } stop && Fps.TryRemove(stop.Pid, out var current)) current.Dispose();
                return new(true);
            case "exit": Stop.Cancel(); NativeTray.Quit(); return new(true);
            default: return new(false, "Unknown background command");
        }
    }

    private static BackgroundReply Memory()
    {
        using var current = Process.GetCurrentProcess();
        return new(true, Pid: current.Id, PrivateBytes: current.PrivateMemorySize64, WorkingSetBytes: current.WorkingSet64);
    }

    private static async Task WatchAsync()
    {
        EventWaitHandle? activate = null;
        using var hostProcess = Process.GetCurrentProcess();
        int sessionId = hostProcess.SessionId;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            int ticks = 0;
            while (await timer.WaitForNextTickAsync(Stop.Token))
            {
                try
                {
                    if (++ticks % 5 == 0 && MultiplayerGameGuard.IsActive)
                    { Log("Multiplayer process guard: stopping background host"); Stop.Cancel(); NativeTray.Quit(); break; }
                    bool ownerAlive = Matches(_ownerPid, _ownerStart);
                    if (_parked && !ownerAlive)
                    {
                        NativeTray.SetVisible(true);
                        activate ??= new(false, EventResetMode.AutoReset, "Local\\HoYoShadeHub.Activate.v1");
                        if (activate.WaitOne(0)) Open();
                    }
                    else { activate?.Dispose(); activate = null; }
                    foreach (var pair in Games.ToArray())
                    {
                        var watch = pair.Value;
                        if (_parked && !ownerAlive) watch.Transferred = true;
                        bool running = HoYoShadeHub.Extensions.Games.GameProcessDiscovery.IsRunning(watch.Process, sessionId);
                        if (!running)
                        {
                            if (!Games.TryRemove(pair.Key, out _)) continue;
                            using var completedProcess = watch.Process;
                            PlayTimeWriter.Write(watch.Plan, 3, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            if (Fps.TryRemove(pair.Key, out var fps)) fps.Dispose();
                            if (watch.Transferred)
                            {
                                try { watch.Snapshot(); }
                                catch (Exception ex) { Log("final snapshot: " + ex); }
                                FinalizeSession(watch.Plan);
                            }
                        }
                        else if (watch.Transferred || (_parked && !ownerAlive))
                        {
                            watch.Transferred = true;
                            if (DateTime.UtcNow >= watch.NextSnapshot) { watch.Snapshot(); watch.NextSnapshot = DateTime.UtcNow.AddMinutes(5); }
                        }
                        if (running && DateTime.UtcNow >= watch.NextPlaytime && watch.Plan.RecordPlayTime)
                        {
                            if (!watch.PlaytimeStarted)
                            {
                                PlayTimeWriter.Write(watch.Plan, 1, new DateTimeOffset(new DateTime(watch.Plan.StartTicks, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
                                watch.PlaytimeStarted = true;
                            }
                            PlayTimeWriter.Write(watch.Plan, 2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            watch.NextPlaytime = DateTime.UtcNow.AddSeconds(30);
                        }
                    }
                    if (!_parked && !ownerAlive && Games.IsEmpty && Fps.IsEmpty && _ownerPid != 0)
                    { Stop.Cancel(); NativeTray.Quit(); break; }
                }
                catch (Exception ex) { Log("watch: " + ex); }
            }
        }
        catch (OperationCanceledException) { }
        finally { activate?.Dispose(); }
    }

    private static void FinalizeSession(GameWatch game)
    {
        if (_fixture || game.SessionDirectory is null) return;
        try
        {
            string plan = Path.Combine(game.SessionDirectory, "background-handoff.json");
            File.WriteAllText(plan, JsonSerializer.Serialize(game, BackgroundJson.Default.GameWatch));
            StartGui(["--finish-lightweight", plan]);
        }
        catch (Exception ex) { Log("finalize: " + ex); }
    }

    private static void Open()
    {
        if (Interlocked.Exchange(ref _restoring, 1) != 0) return;
        if (_fixture) { Log("fixture restore requested"); return; }
        if (StartGui(["--resume-ui"])) { _parked = false; NativeTray.SetVisible(false); }
        else Interlocked.Exchange(ref _restoring, 0);
    }

    private static bool StartGui(string[] args)
    {
        try
        {
            if (!File.Exists(_gui)) throw new FileNotFoundException("GUI executable is missing", _gui);
            var start = new ProcessStartInfo(_gui) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_gui)!, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start);
            return child is not null;
        }
        catch (Exception ex) { Log("restore: " + ex); return false; }
    }

    private static bool Matches(int pid, long ticks)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch { return false; }
    }

    internal static void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(_data)) return;
        try { lock (LogGate) { string dir = Path.Combine(_data, "log"); Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "F6Launcher-background.log"), $"[{DateTime.Now:O}] {message}\n"); } }
        catch { }
    }

    private sealed class Watch(Process process, GameWatch plan)
    {
        internal Process Process { get; } = process;
        internal GameWatch Plan { get; set; } = plan;
        internal bool Transferred;
        internal DateTime NextSnapshot = DateTime.UtcNow;
        internal DateTime NextPlaytime = DateTime.UtcNow;
        internal bool PlaytimeStarted;
        internal void Snapshot()
        {
            if (Plan.SessionDirectory is not { } session) return;
            Directory.CreateDirectory(session);
            foreach (var file in Plan.Files)
            {
                try
                {
                    string source = file.Path;
                    if (file.Multiple)
                    {
                        var directory = Path.GetDirectoryName(source)!;
                        string stem = Path.GetFileNameWithoutExtension(source);
                        source = Directory.EnumerateFiles(directory, "*.log")
                            .Where(p => { string name = Path.GetFileNameWithoutExtension(p);
                                return name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase)
                                    && name.Length > stem.Length + 1 && name[(stem.Length + 1)..].All(char.IsAsciiDigit)
                                    && File.GetLastWriteTimeUtc(p).Ticks >= Plan.StartedUtcTicks
                                    && File.GetLastWriteTimeUtc(p) <= DateTime.UtcNow; })
                            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? source;
                    }
                    var info = new FileInfo(source);
                    if (!info.Exists || (file.IsLog && (info.LastWriteTimeUtc.Ticks < Plan.StartedUtcTicks || info.LastWriteTimeUtc > DateTime.UtcNow))) continue;
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (input.Length > 16 * 1024 * 1024) input.Position = input.Length - 16 * 1024 * 1024;
                    string destination = Path.Combine(session, file.Name);
                    string staged = destination + ".background.tmp";
                    using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output, 64 * 1024);
                    File.Move(staged, destination, overwrite: true);
                }
                catch (Exception ex) { Log("snapshot " + file.Name + ": " + ex.Message); }
            }
            Log("Background snapshot " + Plan.Pid);
        }
    }
}
