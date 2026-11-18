using F6Launcher.Background;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Core.Networking;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Features.RPC;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

internal static class BackgroundModeCoordinator
{
    private sealed class LogToken;
    private static readonly ILogger Log = AppConfig.GetLogger<LogToken>();
    private static readonly SemaphoreSlim ConnectionGate = new(1, 1);
    private static string? _pipe;
    private static bool _connected;
    private static int _work;
    private static long _request;
    private static bool _parking;
    private static string? _suppressedIdentity;
    private static GameWatch? _latest;
    private static readonly ConcurrentDictionary<int, GameWatch> Inherited = new();
    internal static bool IsGuiProcess { get; set; }
    internal static GameWatch? InheritedSession(int pid) => Inherited.TryGetValue(pid, out var plan) ? plan : null;
    private static string PipeName => _pipe ??= "F6Launcher.Background." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserName + "|" + Path.GetFullPath(AppConfig.UserDataFolder).ToUpperInvariant())))[..24];

    internal static IDisposable BeginLaunchWork()
    { Interlocked.Increment(ref _work); return new Work(); }
    private sealed class Work : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _work); }
    }

    internal static async Task InitializeAsync()
    {
        try { await EnsureConnectedAsync(); }
        catch (Exception ex) { Log.LogWarning(ex, "Start lightweight background host"); }
    }

    private static async Task EnsureConnectedAsync()
    {
        if (_connected) return;
        await ConnectionGate.WaitAsync();
        try
        {
            if (_connected) return;
            bool exists;
            try { exists = (await SendCoreAsync(new() { Command = "status" }, 500)).Success; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { exists = false; }
            if (!exists)
            {
                string exe = Path.Combine(AppContext.BaseDirectory, "F6Launcher.BackgroundHost.exe");
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
                start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(PipeName);
                using var child = Process.Start(start) ?? throw new IOException("轻量后台进程没有启动。");
            }
            using var current = Process.GetCurrentProcess();
            var result = await SendCoreAsync(new() { Command = "hello", OwnerPid = current.Id,
                OwnerStartTicks = current.StartTime.ToUniversalTime().Ticks, GuiPath = AppConfig.HoYoShadeHubExecutePath,
                UserData = AppConfig.UserDataFolder }, 8000);
            if (!result.Success) throw new IOException(result.Error);
            _connected = true;
            foreach (var plan in result.Games ?? []) Inherited[plan.Pid] = plan;
            if (result.Game is { } existing)
            {
                _latest = existing;
                _suppressedIdentity = existing.Pid + ":" + existing.StartTicks;
            }
            Log.LogInformation("Lightweight host ready: pid={Pid}, privateMiB={PrivateMiB:F1}, workingSetMiB={WorkingSetMiB:F1}",
                result.Pid, result.PrivateBytes / 1048576d, result.WorkingSetBytes / 1048576d);
        }
        finally { ConnectionGate.Release(); }
    }

    internal static async Task<BackgroundReply> SendAsync(BackgroundRequest request, int timeoutMs = 8000)
    { await EnsureConnectedAsync(); return await SendCoreAsync(request, timeoutMs); }

    private static async Task<BackgroundReply> SendCoreAsync(BackgroundRequest request, int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, BackgroundJson.Default.BackgroundRequest).AsMemory(), timeout.Token);
        string? line = await reader.ReadLineAsync(timeout.Token);
        return line is null ? throw new IOException("轻量后台断开连接。") :
            JsonSerializer.Deserialize(line, BackgroundJson.Default.BackgroundReply) ?? throw new IOException("无效后台响应。");
    }

    internal static void RegisterGame(Process process, GameId? gameId)
    {
        if (gameId is null) return;
        try
        {
            var plan = new GameWatch(process.Id, process.StartTime.ToUniversalTime().Ticks, gameId.GameBiz.Value,
                null, process.StartTime.ToUniversalTime().Ticks, [], [], [], null, []);
            _ = RegisterAsync(plan);
        }
        catch (Exception ex) { Log.LogDebug(ex, "Register lightweight game watch"); }
    }

    internal static async Task RegisterAsync(GameWatch plan)
    {
        if (!IsGuiProcess) return;
        try
        {
            var reply = await SendAsync(new() { Command = "track", Game = plan });
            if (!reply.Success) { Log.LogWarning("Lightweight game watch rejected: {Reason}", reply.Error); return; }
            _latest = reply.Game ?? plan;
            RequestPark();
        }
        catch (Exception ex) { Log.LogWarning(ex, "Register lightweight game watch"); }
    }

    internal static void ShowRequested()
    {
        if (_latest is { } game) _suppressedIdentity = game.Pid + ":" + game.StartTicks;
        Interlocked.Increment(ref _request);
    }

    internal static void RequestPark(bool explicitHide = false)
    {
        if (!IsGuiProcess || !AppConfig.GameLightweightMode || _latest is not { } game || _parking) return;
        string identity = game.Pid + ":" + game.StartTicks;
        if (!explicitHide && _suppressedIdentity == identity) return;
        if (explicitHide) _suppressedIdentity = null;
        long request = Interlocked.Increment(ref _request);
        _ = ParkWhenReadyAsync(request);
    }

    private static async Task ParkWhenReadyAsync(long request)
    {
        try
        {
            // Wait for launch preparation, delayed injection and FPS attachment, not a fixed game-start delay.
            do { await Task.Delay(1000); if (request != _request) return; }
            while (Volatile.Read(ref _work) != 0 || BackgroundOperationActivity.HasActive);
            if (_latest is not { SessionDirectory: not null } game || !AppConfig.GameLightweightMode || _parking) return;
            _parking = true;
            await GameSessionLogCollector.TransferToBackgroundAsync();
            using var current = Process.GetCurrentProcess();
            var result = await SendAsync(new() { Command = "park", OwnerPid = current.Id,
                OwnerStartTicks = current.StartTime.ToUniversalTime().Ticks });
            if (!result.Success) throw new IOException(result.Error);
            // The managed RPC worker must leave with the GUI, including when the
            // user normally keeps it running in the background.
            await AppConfig.GetService<RpcService>().StopRpcServerAsync(DateTime.UtcNow.AddSeconds(3));
            bool operationStarted = BackgroundOperationActivity.HasActive;
            if (request != _request || operationStarted)
            {
                _parking = false;
                using var gui = Process.GetCurrentProcess();
                await SendAsync(new() { Command = "hello", OwnerPid = gui.Id, OwnerStartTicks = gui.StartTime.ToUniversalTime().Ticks,
                    GuiPath = AppConfig.HoYoShadeHubExecutePath, UserData = AppConfig.UserDataFolder });
                if (operationStarted && request == _request) RequestPark();
                return;
            }
            Log.LogInformation("Entering lightweight mode: GUI process {Pid} exits; background pid={BackgroundPid}, privateMiB={PrivateMiB:F1}",
                current.Id, result.Pid, result.PrivateBytes / 1048576d);
            Serilog.Log.CloseAndFlush();
            // No WinUI, bitmap, page, Win2D or managed UI heap remains resident.
            Environment.Exit(0);
        }
        catch (Exception ex) { _parking = false; Log.LogWarning(ex, "Enter lightweight mode; keeping GUI available"); }
    }
}
