using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Core.Networking;

public class EchFallbackHttpMessageHandler : DelegatingHandler
{
    public EchFallbackHttpMessageHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ForkNetworkPolicy.EnsureAllowed(request.RequestUri?.AbsoluteUri);
        if (DohService.EnableEch && request.RequestUri != null)
        {
            var host = request.RequestUri.Host;
            bool isEchSupported = false;
            try
            {
                isEchSupported = await DohService.DetectEchSupportAsync(host, cancellationToken);
            }
            catch
            {
                // Ignored
            }

            if (isEchSupported)
            {
                try
                {
                    var response = await CurlExecuteAsync(request, cancellationToken);
                    if (response != null)
                    {
                        return response;
                    }
                }
                catch
                {
                    // Ignored
                }
            }
        }

        // Default path: standard TLS
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage?> CurlExecuteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string curlPath = Path.Combine(AppContext.BaseDirectory, "curl.exe");
        if (!File.Exists(curlPath))
        {
            return null;
        }

        var url = request.RequestUri!.ToString();
        var dohUrl = DohService.GetCurrentDohUrl();
        var argsBuilder = new StringBuilder();
        argsBuilder.Append("--ech true --ca-native ");
        if (!string.IsNullOrWhiteSpace(dohUrl))
        {
            argsBuilder.Append($"--doh-url \"{dohUrl}\" ");
        }
        
        // Redirects are handled and validated by ForkNetworkPolicyHandler.
        argsBuilder.Append("-s --dump-header - ");

        foreach (var header in request.Headers)
        {
            foreach (var val in header.Value)
            {
                argsBuilder.Append($"-H \"{header.Key}: {val}\" ");
            }
        }

        string? tempFile = null;
        if (request.Method == HttpMethod.Post && request.Content != null)
        {
            string postData = await request.Content.ReadAsStringAsync(cancellationToken);
            tempFile = Path.Combine(TemporaryFolder.Path, $"curl_post_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempFile, postData, Encoding.UTF8, cancellationToken);
            
            if (request.Content.Headers.ContentType != null)
            {
                argsBuilder.Append($"-H \"Content-Type: {request.Content.Headers.ContentType}\" ");
            }
            argsBuilder.Append($"-d @\"{tempFile}\" ");
        }
        else if (request.Method != HttpMethod.Get)
        {
            argsBuilder.Append($"-X {request.Method.Method} ");
        }

        argsBuilder.Append($"\"{url}\"");

        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            Arguments = argsBuilder.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            // Consume stderr asynchronously in background to prevent blocking
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = process.StandardError;
                    var buffer = new char[1024];
                    while (await reader.ReadAsync(buffer, 0, buffer.Length) > 0) {}
                }
                catch {}
            });

            // Read only headers, byte by byte, leaving binary payload bytes in the stream.
            // This also preserves redirect/error status for the outer policy handler.
            var output = process.StandardOutput.BaseStream;
            string header;
            string[] lines;
            int status;
            do
            {
                using var buffer = new MemoryStream();
                byte[] one = new byte[1];
                while (buffer.Length < 65536)
                {
                    if (await output.ReadAsync(one, cancellationToken) == 0)
                        throw new HttpRequestException("curl did not return complete HTTP headers.");
                    buffer.WriteByte(one[0]);
                    if (buffer.Length >= 4)
                    {
                        var bytes = buffer.GetBuffer();
                        int n = (int)buffer.Length;
                        if (bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
                    }
                }
                if (buffer.Length >= 65536) throw new HttpRequestException("curl HTTP headers too large.");
                header = Encoding.Latin1.GetString(buffer.ToArray());
                lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0 || !int.TryParse(lines[0].Split(' ')[1], out status))
                    throw new HttpRequestException("Invalid curl HTTP status.");
            }
            while (status < 200 || lines[0].Contains("Connection established", StringComparison.OrdinalIgnoreCase));
            var responseMessage = new HttpResponseMessage((HttpStatusCode)status)
            {
                RequestMessage = request,
                Content = new StreamContent(new ProcessStream(output, process, tempFile, cancellationToken))
            };
            foreach (string line in lines.Skip(1))
            {
                int separator = line.IndexOf(':');
                if (separator <= 0) continue;
                string key = line[..separator], value = line[(separator + 1)..].Trim();
                if (!responseMessage.Headers.TryAddWithoutValidation(key, value))
                    responseMessage.Content.Headers.TryAddWithoutValidation(key, value);
            }
            return responseMessage;
        }
        catch
        {
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(); } catch {}
                process.Dispose();
            }
            if (tempFile != null && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            return null;
        }
    }
}

public class ProcessStream : Stream
{
    private readonly Stream _innerStream;
    private readonly Process _process;
    private readonly string? _tempFile;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private bool _disposed;

    public ProcessStream(Stream innerStream, Process process, string? tempFile, CancellationToken cancellationToken)
    {
        _innerStream = innerStream;
        _process = process;
        _tempFile = tempFile;

        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch { }
            });
        }
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;
    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _innerStream.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _innerStream.ReadAsync(buffer, cancellationToken);
    
    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _cancellationRegistration.Dispose();
                _innerStream.Dispose();
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch { }
                _process.Dispose();
                if (_tempFile != null && File.Exists(_tempFile))
                {
                    try { File.Delete(_tempFile); } catch { }
                }
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _cancellationRegistration.Dispose();
            await _innerStream.DisposeAsync();
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch { }
            _process.Dispose();
            if (_tempFile != null && File.Exists(_tempFile))
            {
                try { File.Delete(_tempFile); } catch { }
            }
            _disposed = true;
        }
        await base.DisposeAsync();
    }
}
