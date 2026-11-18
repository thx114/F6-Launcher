using System.Diagnostics;

namespace HoYoShadeHub.Core.Networking;

public sealed record NetworkRequestSnapshot(long Id, string Name, string Endpoint, DateTimeOffset Started,
    double ElapsedMilliseconds, string Status, bool Active, long OperationId = 0,
    double? Percent = null, long BytesReceived = 0, bool IsDownload = false);

/// <summary>Bounded, process-local HTTP activity for the launcher status panel.</summary>
public static class NetworkRequestActivity
{
    private static readonly object Gate = new();
    private static readonly Dictionary<long, NetworkRequestSnapshot> Active = [];
    private static readonly Queue<NetworkRequestSnapshot> Recent = [];
    private static long _next;

    public static IReadOnlyList<NetworkRequestSnapshot> Snapshot()
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            return Active.Values.OrderBy(x => x.Id)
                .Select(x => x with { ElapsedMilliseconds = (now - x.Started).TotalMilliseconds })
                .Concat(Recent.Reverse()).Take(32).ToArray();
        }
    }

    public static IReadOnlyList<NetworkRequestSnapshot> BackgroundSnapshot()
    {
        var operations = BackgroundOperationActivity.Snapshot();
        var ids = operations.Select(x => x.Id).ToHashSet();
        return operations.Where(x => !x.InlineVisible).Select(x => new NetworkRequestSnapshot(
            -x.Id, x.Name, "", x.Started, (DateTimeOffset.UtcNow - x.Started).TotalMilliseconds,
            x.Status, true, x.Id, x.Percent, x.BytesReceived, true))
            .Concat(Snapshot().Where(x => !ids.Contains(x.OperationId))).ToArray();
    }

    internal static long Begin(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        // Never display query strings, authentication headers or URL user info.
        string path = uri?.AbsolutePath ?? string.Empty;
        string name = Path.GetFileName(path) switch
        {
            "plugins.json" => "插件目录", "optiscaler.json" => "OptiScaler 目录",
            "modules.json" => "模块目录", "conditions.json" => "插件条件表",
            "dll-fingerprints.json" => "NR / DLL 指纹", "dlss_manifest.json" => "DLL 版本清单",
            "getGames" => "游戏名称与区服信息", "getAllGameBasicInfo" => "游戏背景信息",
            "getGameContent" => "游戏资讯", "getGameConfigs" => "游戏启动配置",
            "getGamePackages" => "游戏安装包信息", "getDXConfigs" => "游戏 DX12 配置",
            var file when file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) => "下载 " + file,
            _ when path.Contains("background", StringComparison.OrdinalIgnoreCase) => "游戏背景",
            _ when path.Contains("release", StringComparison.OrdinalIgnoreCase) => "版本更新",
            _ => request.Method + " " + (Path.GetFileName(path) is { Length: > 0 } leaf ? leaf : "请求"),
        };
        long id = Interlocked.Increment(ref _next);
        lock (Gate)
            Active[id] = new(id, name, uri is null ? string.Empty : uri.Host + path,
                DateTimeOffset.UtcNow, 0, "请求中", true, BackgroundOperationActivity.CurrentId);
        return id;
    }

    internal static void End(long id, string status)
    {
        lock (Gate)
        {
            if (!Active.Remove(id, out var item)) return;
            Recent.Enqueue(item with { ElapsedMilliseconds = (DateTimeOffset.UtcNow - item.Started).TotalMilliseconds,
                Status = status, Active = false });
            while (Recent.Count > 12) Recent.Dequeue();
        }
    }
}

internal sealed class NetworkActivityHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long id = NetworkRequestActivity.Begin(request);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string status = response.IsSuccessStatusCode ? "完成" : "HTTP " + (int)response.StatusCode;
            response.Content = new ActivityContent(response.Content, id, status);
            return response;
        }
        catch (OperationCanceledException) { NetworkRequestActivity.End(id, "已取消 / 超时"); throw; }
        catch { NetworkRequestActivity.End(id, "失败"); throw; }
    }

    private sealed class ActivityContent : HttpContent
    {
        private readonly HttpContent _content;
        private readonly long _id;
        private readonly string _status;
        internal ActivityContent(HttpContent content, long id, string status)
        {
            _content = content; _id = id; _status = status;
            foreach (var header in content.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = _content.Headers.ContentLength ?? -1; return length >= 0; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            try { await _content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false); NetworkRequestActivity.End(_id, _status); }
            catch (OperationCanceledException) { NetworkRequestActivity.End(_id, "已取消 / 超时"); throw; }
            catch { NetworkRequestActivity.End(_id, "读取失败"); throw; }
        }
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new ActivityStream(await _content.ReadAsStreamAsync().ConfigureAwait(false), _id, _status);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { NetworkRequestActivity.End(_id, _status); _content.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class ActivityStream(Stream inner, long id, string status) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            try { int read = inner.Read(buffer, offset, count); if (read == 0) NetworkRequestActivity.End(id, status); return read; }
            catch { NetworkRequestActivity.End(id, "读取失败"); throw; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) NetworkRequestActivity.End(id, status); return read; }
            catch (OperationCanceledException) { NetworkRequestActivity.End(id, "已取消 / 超时"); throw; }
            catch { NetworkRequestActivity.End(id, "读取失败"); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        { if (disposing) { NetworkRequestActivity.End(id, status); inner.Dispose(); } base.Dispose(disposing); }
    }
}
