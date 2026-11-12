using System.Diagnostics;
using System.Net;
using System.Text;
using System.IO.Compression;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Networking;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class OptiDownloadTests
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "f6-opti-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? oldCache = GithubReleaseResolver.CacheDirectory;
        string? oldProxy = HysxHttp.ProxyUrl;
        int passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            passed++;
        }
        try
        {
            GithubReleaseResolver.CacheDirectory = Path.Combine(root, "metadata");
            HysxHttp.ProxyUrl = null;
            const string main = "v0.1.37.2-Optiscaler-F5.zip";
            var source = OptiScalerCatalog.LoadFile(Path.Combine(Directory.GetCurrentDirectory(), "catalog", "optiscaler.json"))!
                .Single(s => s.Id == "janblade-f5");
            Check(GlobMatcher.IsSimpleMatch(source.AssetPattern!, main), "Current main archive matches catalog");
            Check(GlobMatcher.IsSimpleMatch(source.AssetPattern!, "OptiScaler-DLSSNR-F5-v0.1.36.zip"), "Old main archive remains compatible");
            Check(!GlobMatcher.IsSimpleMatch(source.AssetPattern!, "Optional-F5Low-Update-1.1.0.184.zip"), "Optional update excluded from main pattern");
            string html = string.Join('\n', new[] { main, "Optional-F5Low-Update-1.1.0.184.zip", "Optional-Vendor-Neutral-DLSS5-Forwarder.zip" }
                .Select(n => $"<a href=\"/owner/repo/releases/download/v1/{n}\">asset</a>"));
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(html) })));
            var downloader = new OptiScalerDownloader(new GithubReleaseResolver(client));
            var choices = await downloader.ListAssetChoicesAsync(source, "test-" + Guid.NewGuid().ToString("N"));
            Check(choices.PatternMatched && choices.Names.SequenceEqual([main]), "Current pattern selects only main zip");
            var stale = new OptiScalerSource { Id = "stale", Repository = "owner/stale", AssetPattern = "obsolete-*.zip" };
            var fallback = await downloader.ListAssetChoicesAsync(stale, "test-" + Guid.NewGuid().ToString("N"));
            Check(!fallback.PatternMatched && fallback.Names.Count == 3, "Stale filter offers actual files for explicit choice");
            bool refused = false;
            try { await downloader.InstallAsync(stale, "test-" + Guid.NewGuid().ToString("N"), null, new OptiScalerLibrary(Path.Combine(root, "library"))); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && !Directory.Exists(Path.Combine(root, "library")), "Stale rule cannot silently install an optional component");
            using var stalled = new HttpClient(new Handler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }));
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(60)))
            {
                bool canceled = false;
                try { await new GithubReleaseResolver(stalled).GetAssetNamesFromHtmlAsync("owner/cancel", Guid.NewGuid().ToString("N"), cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Asset lookup propagates cancellation instead of returning empty success");
            }
            string target = Path.Combine(root, "download.zip");
            File.WriteAllText(target, "keep-original");
            bool headerTimedOut = false;
            try { await new DownloadService(stalled, TimeSpan.FromMilliseconds(60)).DownloadToFileAsync("https://fixture.invalid/file", target); }
            catch (TimeoutException) { headerTimedOut = true; }
            Check(headerTimedOut && File.ReadAllText(target) == "keep-original", "Header timeout preserves previous file");
            using var stalledBody = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new NeverReadStream()) })));
            bool readTimedOut = false;
            try { await new DownloadService(stalledBody, readTimeout: TimeSpan.FromMilliseconds(60)).DownloadToFileAsync("https://fixture.invalid/file", target); }
            catch (TimeoutException) { readTimedOut = true; }
            Check(readTimedOut && File.ReadAllText(target) == "keep-original", "Idle body timeout preserves previous file");
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
            {
                bool canceled = false;
                try { await new DownloadService(stalled).DownloadToFileAsync("https://fixture.invalid/file", target, cancellationToken: cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "User cancel remains cancellation, not a timeout error");
            }

            string cache = Path.Combine(root, "cache"), build = Path.Combine(root, "new-build");
            Directory.CreateDirectory(build);
            string nr = Path.Combine(build, NrRuntimeLibrary.FileName);
            File.WriteAllText(nr, "new-NR-content");
            string unrelated = Path.Combine(root, "other-build");
            Directory.CreateDirectory(unrelated);
            string unrelatedNr = Path.Combine(unrelated, NrRuntimeLibrary.FileName);
            File.WriteAllText(unrelatedNr, "leave-unrelated-NR");
            var reports = new List<NrRuntimeLibrary.ConsolidationProgress>();
            using (File.Open(unrelatedNr, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var errors = NrRuntimeLibrary.ConsolidateScoped(cache, [build, build, Path.Combine(build, "nested")],
                    new ImmediateProgress<NrRuntimeLibrary.ConsolidationProgress>(reports.Add));
                Check(errors.Count == 0, "Scoped consolidation never opens unrelated locked NR");
            }
            Check(reports.Last().Total == 1 && reports.Last().Completed == 1, "Duplicate/overlapping roots consolidate each file once");
            string canonical = Path.Combine(cache, "nr-content", NrRuntimeLibrary.Hash(nr), NrRuntimeLibrary.FileName);
            Check(HysxFileLink.AreHardLinked(nr, canonical), "Scoped install retains verified NR hardlink identity");
            Check(File.ReadAllText(unrelatedNr) == "leave-unrelated-NR", "Unrelated build preserved");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                bool canceled = false;
                try { NrRuntimeLibrary.ConsolidateScoped(cache, [build], cancellationToken: cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Consolidation accepts cancellation");
            }
            // Exercise actual local installer routing, with no game or network dependencies.
            string package = Path.Combine(root, "module-with-nr.zip");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry(NrRuntimeLibrary.FileName).Open())) writer.Write("module-NR");
                using (var writer = new StreamWriter(zip.CreateEntry("module.dll").Open())) writer.Write("fixture");
            }
            using (File.Open(unrelatedNr, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var installed = await new LocalPackageInstaller(unrelated, Path.Combine(root, "addons"), Path.Combine(root, "modules"), cacheRoot: cache)
                    .InstallAsync(package, LocalPackageKind.Module);
                string installedNr = Path.Combine(installed.TargetPath, NrRuntimeLibrary.FileName);
                Check(HysxFileLink.AreHardLinked(installedNr,
                    Path.Combine(cache, "nr-content", NrRuntimeLibrary.Hash(installedNr), NrRuntimeLibrary.FileName)),
                    "Local import consolidates only installed target and preserves strict NR contract");
            }
            Console.WriteLine($"PASS {passed} Opti asset, timeout, cancellation and scoped NR checks; UI/GameTested=false");
            return 0;
        }
        finally
        {
            GithubReleaseResolver.CacheDirectory = oldCache;
            HysxHttp.ProxyUrl = oldProxy;
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task<int> ProbeAsync(string destination)
    {
        // Explicit command only. Download and inspect one ZIP; never load DLLs or run setup programs.
        string root = Path.GetFullPath(destination);
        if (Directory.Exists(root)) throw new IOException("Probe directory must be new.");
        Directory.CreateDirectory(root);
        GithubReleaseResolver.CacheDirectory = Path.Combine(root, "metadata");
        HysxHttp.ProxyUrl = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var source = OptiScalerCatalog.LoadFile(Path.Combine(Directory.GetCurrentDirectory(), "catalog", "optiscaler.json"))!
            .Single(s => s.Id == "janblade-f5");
        var watch = Stopwatch.StartNew();
        var downloader = new OptiScalerDownloader();
        var choices = await downloader.ListAssetChoicesAsync(source, "v0.1.37.2", cancellation.Token);
        Console.WriteLine($"LIVE assets: {string.Join(",", choices.Names)}; matched={choices.PatternMatched}; elapsedMs={watch.ElapsedMilliseconds}");
        string asset = choices.PatternMatched && choices.Names.Count == 1 ? choices.Names.Single()
            : throw new InvalidDataException("Expected exactly the main ZIP.");
        long last = 0;
        var build = await downloader.InstallAsync(source, "v0.1.37.2", asset, new OptiScalerLibrary(Path.Combine(root, "library")),
            new ImmediateProgress<DownloadProgress>(p =>
            {
                if (p.BytesReceived - last < 8 * 1024 * 1024) return;
                last = p.BytesReceived;
                Console.WriteLine($"LIVE bytes={p.BytesReceived}, total={p.TotalBytes}, elapsedMs={watch.ElapsedMilliseconds}");
            }), cancellation.Token);
        if (build.DllPath is null || !File.Exists(build.DllPath)) throw new InvalidDataException("Main DLL missing after extraction.");
        Console.WriteLine($"LIVE installed={build.Id}; dll={build.DllPath}; sha256={NrRuntimeLibrary.Hash(build.DllPath)}; bytes={build.SizeBytes}; elapsedMs={watch.ElapsedMilliseconds}; GameTested=false");
        return 0;
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class NeverReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
