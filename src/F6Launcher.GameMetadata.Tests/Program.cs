using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using HoYoShadeHub;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Features.HoYoPlay;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
int passed = 0;
void Check(bool ok, string message)
{ if (!ok) throw new InvalidOperationException(message); passed++; Console.WriteLine("PASS " + message); }
GameInfo Info(GameBiz biz, string name) => new()
{
    Id = GameId.FromGameBiz(biz)!.Id, GameBiz = biz,
    Display = new() { Name = name, Language = "zh-cn", Icon = new() { Url = "fixture.png" } },
    GameServerConfigs = [], DisplayStatus = GameInfoDisplayStatus.LAUNCHER_GAME_DISPLAY_STATUS_AVAILABLE,
};
using var handler = new MetadataHandler(Info);
using var http = new HttpClient(handler);
using var memory = new MemoryCache(new MemoryCacheOptions());
var service = new HoYoPlayService(NullLogger<HoYoPlayService>.Instance, new HoYoPlayClient(http), http, memory);
var genshin = GameId.FromGameBiz(GameBiz.hk4e_cn)!;
AppConfig.Value = JsonSerializer.Serialize(new[] { Info(GameBiz.hk4e_cn, "本地缓存") });
Task<GameInfo> local;
var oldContext = SynchronizationContext.Current;
var context = new UnpumpedContext();
SynchronizationContext.SetSynchronizationContext(context);
try { local = service.GetGameInfoAsync(genshin); }
finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
Check((await local.WaitAsync(TimeSpan.FromSeconds(5))).Display.Name == "本地缓存", "Offline page reads persistent game information");
Check(handler.Calls == 0, "Cached information needs no HTTP request");
Check(AppConfig.ReadOnWorker && context.Posts == 0, "Persistence and parsing do not need the UI dispatcher");
memory.Set("unrelated-background-and-DX12-cache", "keep");
using var canceledPage = new CancellationTokenSource();
var canceled = service.UpdateGameInfoListAsync(canceledPage.Token);
var readers = Enumerable.Range(0, 20).Select(_ => service.UpdateGameInfoListAsync()).ToArray();
await handler.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(handler.Calls == 5, "Concurrent page refreshes share exactly one request per launcher");
Check(handler.Pending == 5, "Launcher metadata requests run concurrently rather than serially");
canceledPage.Cancel();
try { await canceled; throw new InvalidOperationException("Canceled page completed"); }
catch (OperationCanceledException) { }
Check(handler.Pending == 5, "Leaving one page does not cancel the shared metadata refresh");
Check((await service.GetGameInfoAsync(genshin)).Display.Name == "本地缓存", "Cached data remains usable during a slow refresh");
Check(memory.Get<string>("unrelated-background-and-DX12-cache") == "keep", "Refresh preserves other feature caches");
handler.Release.SetResult();
var lists = await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));
Check(lists.All(x => x.Count == 5), "Every waiting page receives the complete game list");
Check((await service.GetGameInfoAsync(genshin)).Display.Name == "网络缓存", "Completed refresh replaces stale game information");
await service.UpdateGameInfoListAsync();
Check(handler.Calls == 5 && AppConfig.WriteOnWorker, "Repeated visits reuse the refresh and persistence stays off UI");

// A second service proves that cancellation does not leave a permanently cached failed task.
using var failureHandler = new MetadataHandler(Info) { Fail = true };
using var failureHttp = new HttpClient(failureHandler);
using var failureMemory = new MemoryCache(new MemoryCacheOptions());
var recovering = new HoYoPlayService(NullLogger<HoYoPlayService>.Instance, new HoYoPlayClient(failureHttp), failureHttp, failureMemory);
failureHandler.Release.SetResult();
string? beforeFailure = AppConfig.Value;
try { await recovering.UpdateGameInfoListAsync(); throw new InvalidOperationException("Failed refresh completed"); }
catch (HttpRequestException) { }
Check(AppConfig.Value == beforeFailure, "Failed network refresh preserves the persisted snapshot");
failureHandler.Fail = false;
Check((await recovering.UpdateGameInfoListAsync()).Count == 5 && failureHandler.Calls == 10, "Failed refresh can retry");

var root = Path.GetFullPath(args.Single());
if (Directory.Exists(root)) throw new InvalidOperationException("A new fixture directory is required");
var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
File.WriteAllBytes(Path.Combine(first, "shared.bin"), new byte[4096]);
File.WriteAllBytes(Path.Combine(second, "own.bin"), new byte[128]);
Check(Native.CreateHardLinkW(Path.Combine(second, "shared.bin"), Path.Combine(first, "shared.bin"), 0), "Fixture contains a real NTFS hardlink");
File.WriteAllBytes(Path.Combine(outside, "excluded.bin"), new byte[32768]);
using (var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
    { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "mklink", "/J", Path.Combine(first, "junction"), outside },
        RedirectStandardOutput = true, RedirectStandardError = true }))
{
    await junction!.WaitForExitAsync();
    Check(junction.ExitCode == 0, "Fixture contains a directory junction");
}
InstalledGameDirectory[] dirs = [new("first", "same-game", first), new("second", "same-game", second)];
var sizes = await Task.Run(() => InstalledGameSizeScanner.Measure(dirs));
Check(sizes.Games.Count == 2 && sizes.LogicalBytes == 8320, "Streaming scan includes ordinary files without traversing junctions");
Check(sizes.ActualBytes == 4224 && sizes.LogicalBytes - sizes.ActualBytes == 4096, "Space accounting deduplicates actual hardlinks");
using var cancelScan = new CancellationTokenSource();
cancelScan.Cancel();
try { InstalledGameSizeScanner.Measure(dirs, cancelScan.Token); throw new InvalidOperationException("Scan was not canceled"); }
catch (OperationCanceledException) { passed++; Console.WriteLine("PASS Hidden-panel scan honors cancellation"); }
Check(InstalledGameSizeScanner.Measure([new("missing", "none", Path.Combine(root, "missing"))]).Games.Count == 0,
    "Unavailable installation does not break other sizes");
Console.WriteLine($"{passed} checks passed");

sealed class UnpumpedContext : SynchronizationContext
{
    public int Posts;
    public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
}

sealed class MetadataHandler(Func<GameBiz, string, GameInfo> info) : HttpMessageHandler
{
    public int Calls, Pending;
    public bool Fail;
    public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource AllStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.Segments.Last() != "getGames") throw new InvalidOperationException("Unexpected request");
        int count = Interlocked.Increment(ref Calls);
        Interlocked.Increment(ref Pending);
        if (count == 5) AllStarted.TrySetResult();
        try
        {
            await Release.Task.WaitAsync(token);
            if (Fail) throw new HttpRequestException("Offline fixture");
            string query = request.RequestUri.Query;
            GameBiz biz = query.Contains(LauncherId.GlobalOfficial) ? GameBiz.hk4e_global
                : query.Contains(LauncherId.BilibiliGenshin) ? GameBiz.hk4e_bilibili
                : query.Contains(LauncherId.BilibiliStarRail) ? GameBiz.hkrpg_bilibili
                : query.Contains(LauncherId.BilibiliZZZ) ? GameBiz.nap_bilibili : GameBiz.hk4e_cn;
            string json = JsonSerializer.Serialize(new { retcode = 0, message = "OK", data = new { games = new[] { info(biz, "网络缓存") } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        finally { Interlocked.Decrement(ref Pending); }
    }
}

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateHardLinkW(string name, string existing, nint reserved);
}
