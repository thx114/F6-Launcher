using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoYoShadeHub.Core.Networking;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Extensions.Networking;
using HoYoShadeHub;
using HoYoShadeHub.Features.Xxmi;
using HoYoShadeHub.Helpers;

string output = Path.GetFullPath(args.Single());
Directory.CreateDirectory(output);
int passed = 0;
void Check(bool ok, string message)
{ if (!ok) throw new InvalidOperationException(message); passed++; Console.WriteLine("PASS " + message); }

var cache = new AsyncValueCache<int>();
int calls = 0;
var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
Task<int> Fetch() { Interlocked.Increment(ref calls); return release.Task; }
var readers = Enumerable.Range(0, 20).Select(_ => cache.GetAsync(Fetch, TimeSpan.FromMinutes(1))).ToArray();
release.SetResult(7);
Check((await Task.WhenAll(readers)).All(x => x == 7) && calls == 1, "Concurrent page visits share one refresh");
Check(await cache.GetAsync(Fetch, TimeSpan.FromMinutes(1)) == 7 && calls == 1, "Reopening page uses valid cached value");
Check(await cache.GetAsync(() => Task.FromResult(8), TimeSpan.FromMinutes(1), force: true) == 8, "Explicit refresh replaces cached result");
cache.Invalidate();
var stale = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
var oldRead = cache.GetAsync(() => stale.Task, TimeSpan.FromMinutes(1));
cache.Invalidate();
Check(await cache.GetAsync(() => Task.FromResult(9), TimeSpan.FromMinutes(1)) == 9, "Network setting change starts independent refresh");
stale.SetResult(2);
await oldRead;
Check(await cache.GetAsync(() => Task.FromResult(0), TimeSpan.FromMinutes(1)) == 9, "Outdated refresh cannot overwrite new setting cache");
cache.Invalidate();
try { await cache.GetAsync(() => Task.FromException<int>(new IOException("fixture")), TimeSpan.FromMinutes(1)); }
catch (IOException) { }
Check(await cache.GetAsync(() => Task.FromResult(10), TimeSpan.FromMilliseconds(20)) == 10, "Failed refresh is retryable");
await Task.Delay(60);
Check(await cache.GetAsync(() => Task.FromResult(11), TimeSpan.FromMinutes(1)) == 11, "Expired value refreshes");

byte[] first = Enumerable.Repeat((byte)0x26, 512).ToArray(), second = Enumerable.Repeat((byte)0x15, 512).ToArray();
byte[] expected = first.Concat(second).ToArray();
var sendRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
var server = Task.Run(async () =>
{
    using var peer = await listener.AcceptTcpClientAsync();
    using var stream = peer.GetStream();
    var buffer = new byte[4096];
    string headers = "";
    while (!headers.Contains("\r\n\r\n", StringComparison.Ordinal))
    {
        int count = await stream.ReadAsync(buffer);
        if (count == 0) throw new IOException("Fixture client closed before headers");
        headers += Encoding.ASCII.GetString(buffer, 0, count);
    }
    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1024\r\nETag: \"fixture\"\r\nConnection: close\r\n\r\n"));
    await stream.WriteAsync(first);
    await stream.FlushAsync();
    await sendRest.Task.WaitAsync(TimeSpan.FromSeconds(20));
    await stream.WriteAsync(second);
});
HysxHttp.ProxyUrl = null;
using var http = new HttpClient(DohService.CreateSocketsHttpHandler());
var downloader = new DownloadService(http);
var firstProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
long observed = 0;
var progress = new InlineProgress<DownloadProgress>(p =>
{ Interlocked.Exchange(ref observed, p.BytesReceived); if (p.BytesReceived >= 512) firstProgress.TrySetResult(); });
string file = Path.Combine(output, "download.bin");
using (var operation = BackgroundOperationActivity.Begin("plugins", "下载 fixture", inlineVisible: true))
{
    var task = downloader.DownloadToFileAsync($"http://127.0.0.1:{port}/fixture.zip?token=do-not-display", file,
        Convert.ToHexString(SHA256.HashData(expected)), progress, allowResume: false);
    await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(BackgroundOperationActivity.Snapshot().Single().Percent == 50, "Streaming byte progress recorded outside page");
    Check(NetworkRequestActivity.Snapshot().Any(x => x.Active && x.OperationId == operation.Id), "HTTP request belongs to download operation");
    Check(!NetworkRequestActivity.BackgroundSnapshot().Any(x => x.Active), "Visible inline download has no background duplicate");
    BackgroundOperationActivity.SetOwnerVisible("plugins", false);
    var hidden = NetworkRequestActivity.BackgroundSnapshot().Where(x => x.Active).ToArray();
    Check(hidden.Length == 1 && hidden[0].IsDownload && hidden[0].Percent == 50, "Navigating away shows one background progress row");
    Check(!task.IsCompleted && observed == 512, "Navigation keeps download alive");
    BackgroundOperationActivity.SetOwnerVisible("plugins", true);
    Check(!NetworkRequestActivity.BackgroundSnapshot().Any(x => x.Active), "Returning hides background row without restarting download");
    Check(BackgroundOperationActivity.Snapshot().Single().BytesReceived == 512, "Returning preserves inline byte state");
    BackgroundOperationActivity.SetOwnerVisible("plugins", false);
    sendRest.SetResult();
    Check(await task == 1024, "Download finishes while origin page is hidden");
    Check(File.ReadAllBytes(file).SequenceEqual(expected), "Final artifact matches expected bytes and SHA256");
    Check(!File.Exists(file + ".part") && observed == 1024, "Final progress and partial cleanup preserved");
    Check(BackgroundOperationActivity.Snapshot().Single().Status == "正在校验 / 安装", "Operation remains active through installation stage");
}
await server;
Check(BackgroundOperationActivity.Snapshot().Count == 0, "Completed operation releases registry entry");
Check(!NetworkRequestActivity.BackgroundSnapshot().Any(x => x.Active), "Panel becomes idle after completion");
Check(NetworkRequestActivity.Snapshot().All(x => !x.Endpoint.Contains("do-not-display", StringComparison.Ordinal)), "Activity endpoint excludes query secrets");
using (var outer = BackgroundOperationActivity.Begin("one", "one", false))
{
    using (var inner = BackgroundOperationActivity.Begin("two", "two", false))
    {
        Check(NetworkRequestActivity.BackgroundSnapshot().Count(x => x.Active) == 2, "Independent operations remain distinct");
        BackgroundOperationActivity.SetOwnerVisible("one", true);
        Check(NetworkRequestActivity.BackgroundSnapshot().Count(x => x.Active) == 1, "Returning to one page preserves other background operation");
    }
    Check(BackgroundOperationActivity.CurrentId == outer.Id, "Nested operation restores parent context");
}
Check(BackgroundOperationActivity.CurrentId == 0, "Operation context released");
using (var chunkClient = new HttpClient(new ChunkHandler()))
{
    int reports = 0;
    long finalBytes = 0;
    var measured = System.Diagnostics.Stopwatch.StartNew();
    await new DownloadService(chunkClient).DownloadToFileAsync("https://fixture.invalid/chunks", Path.Combine(output, "chunks.bin"),
        progress: new InlineProgress<DownloadProgress>(p => { reports++; finalBytes = p.BytesReceived; }), allowResume: false);
    measured.Stop();
    Check(reports <= Math.Ceiling(measured.Elapsed.TotalMilliseconds / 100) + 2 && finalBytes == 65536,
        "Thousands of tiny reads produce bounded UI progress and a final byte report");
}
AppConfig.UserDataFolder = Path.Combine(output, "user");
using (var pingHandler = new PingHandler())
using (var pingClient = new HttpClient(pingHandler))
{
    await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CloudProxyManager.PingServerCachedAsync(0, pingClient)));
    await CloudProxyManager.PingServerCachedAsync(0, pingClient);
    Check(pingHandler.Count == 1, "About page revisits and concurrent loads reuse one latency probe");
    string latencyFile = Directory.GetFiles(Path.Combine(AppConfig.UserDataFolder, ".hysx", "network")).Single();
    using var stored = JsonDocument.Parse(File.ReadAllText(latencyFile));
    Check(stored.RootElement.GetProperty("Expires").GetDateTimeOffset() > DateTimeOffset.UtcNow,
        "About latency probe persists a local cache with expiration");
    AppConfig.EnableEch = true;
    await CloudProxyManager.PingServerCachedAsync(0, pingClient);
    Check(pingHandler.Count == 2, "Changed network settings invalidate latency cache key");
}
string xxmi = Path.Combine(output, "xxmi");
Directory.CreateDirectory(xxmi);
File.WriteAllText(Path.Combine(xxmi, "XXMI Launcher Config.json"), "{}");
foreach (string name in new[] { "GIMI", "SRMI-PACKAGE-V1", "ZZMI" })
{
    Directory.CreateDirectory(Path.Combine(xxmi, name));
    File.WriteAllText(Path.Combine(xxmi, name, "d3dx.ini"), "; offline fixture");
}
AppConfig.XxmiRoot = xxmi;
Check(XxmiLocator.FindRoot(allowDriveScan: false) == xxmi, "XXMI navigation uses remembered root");
Check(XxmiLocator.ListInstances(xxmi).Count == 3, "XXMI enumerates regular and versioned instances");
AppConfig.Instances["hk4e_cn"] = Path.Combine(xxmi, "ZZMI");
Check(XxmiLocator.FindInstance("hk4e_cn", null, out var genshin, false) == Path.Combine(xxmi, "GIMI") && genshin == "GIMI",
    "XXMI fast lookup rejects another game's manual instance");
Check(XxmiLocator.FindInstance("hkrpg_cn", null, out var rail, false) == Path.Combine(xxmi, "SRMI-PACKAGE-V1") && rail == "SRMI",
    "XXMI fast lookup preserves game isolation for versioned instances");
AppConfig.Instances["hk4e_cn"] = Path.Combine(xxmi, "GIMI");
Check(XxmiLocator.FindInstance("hk4e_cn", null, out _, false) == Path.Combine(xxmi, "GIMI"), "XXMI explicit instance remains authoritative");
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = passed, Offline = true, GameStarted = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {passed} activity/cache checks");
return 0;

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{ public void Report(T value) => report(value); }

sealed class PingHandler : HttpMessageHandler
{
    public int Count;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Interlocked.Increment(ref Count); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
}

sealed class ChunkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new StreamContent(new ChunkStream());
        content.Headers.ContentLength = 65536;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
sealed class ChunkStream : Stream
{
    private int _position;
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => 65536;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = Math.Min(Math.Min(count, 32), 65536 - _position);
        buffer.AsSpan(offset, read).Fill(0x26);
        _position += read;
        return read;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int read = Math.Min(Math.Min(buffer.Length, 32), 65536 - _position);
        buffer.Span[..read].Fill(0x26);
        _position += read;
        return ValueTask.FromResult(read);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
