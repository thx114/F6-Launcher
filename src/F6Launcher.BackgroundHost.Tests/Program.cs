using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using F6Launcher.Background;
using HoYoShadeHub.Core.Networking;
using HoYoShadeHub.Extensions.Dlls;
using Microsoft.Data.Sqlite;

if (args is ["--fixture-child"])
{
    Console.WriteLine("ready");
    await Console.In.ReadLineAsync();
    return 0;
}
if (args.Length != 2) throw new ArgumentException("Provide the native host and output directory.");
string hostPath = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
int passed = 0;
void Check(bool ok, string message)
{ if (!ok) throw new InvalidOperationException(message); passed++; Console.WriteLine("PASS " + message); }
Process Child()
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(BackgroundRequest).Assembly.Location);
    start.ArgumentList.Add("--fixture-child");
    var child = Process.Start(start)!;
    if (child.StandardOutput.ReadLine() != "ready") throw new IOException("Fixture did not initialize.");
    return child;
}
async Task ExitChild(Process child)
{ if (!child.HasExited) { await child.StandardInput.WriteLineAsync("exit"); await child.StandardInput.FlushAsync(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
string pipeName = "F6BackgroundFixture." + Guid.NewGuid().ToString("N");
async Task<BackgroundReply> Send(BackgroundRequest request)
{
    using var timeout = new CancellationTokenSource(10000);
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(timeout.Token);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(pipe, leaveOpen: true);
    await writer.WriteLineAsync(JsonSerializer.Serialize(request, BackgroundJson.Default.BackgroundRequest));
    return JsonSerializer.Deserialize((await reader.ReadLineAsync(timeout.Token))!, BackgroundJson.Default.BackgroundReply)!;
}
using var owner = Child();
using var game = Child();
var startHost = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true };
startHost.ArgumentList.Add("--pipe"); startHost.ArgumentList.Add(pipeName); startHost.ArgumentList.Add("--fixture");
using var host = Process.Start(startHost)!;
long peakPrivate = 0, peakWorking = 0;
try
{
    var invalid = await Send(new() { Command = "hello", OwnerPid = owner.Id, OwnerStartTicks = 1 });
    Check(!invalid.Success, "reject recycled GUI identity");
    var ready = await Send(new() { Command = "hello", OwnerPid = owner.Id, OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks,
        GuiPath = Environment.ProcessPath, UserData = output });
    Check(ready.Success && ready.Pid == host.Id, "connect to exact native host");
    string[] forbidden = ["coreclr.dll", "Microsoft.UI.Xaml.dll", "Microsoft.WindowsAppRuntime.dll", "WinUI.dll", "clr.dll"];
    var modules = host.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToArray();
    Check(!modules.Any(m => forbidden.Contains(m, StringComparer.OrdinalIgnoreCase)), "native host has no CLR or WinUI modules");
    string source = Path.Combine(output, "sources"), session = Path.Combine(output, "session");
    Directory.CreateDirectory(source);
    string old = Path.Combine(source, "old.log"), fresh = Path.Combine(source, "current.log"), config = Path.Combine(source, "OptiScaler.ini");
    File.WriteAllText(old, "OLD_SESSION_SENTINEL"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
    File.WriteAllText(fresh, "CURRENT_SESSION_SENTINEL"); File.WriteAllText(config, "[OptiScaler]\nSentinel=fixture");
    string database = Path.Combine(output, "playtime.db");
    using (var connection = new SqliteConnection($"Data Source={database}"))
    {
        connection.Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE PlayTimeItem (TimeStamp INTEGER, GameBiz TEXT, Pid INTEGER, State INTEGER, CursorPos INTEGER, Message TEXT, PRIMARY KEY(TimeStamp, GameBiz, Pid));";
        cmd.ExecuteNonQuery();
    }
    // Use the same SQLite engine as the application, beside the native executable.
    string sqlite = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "e_sqlite3.dll");
    if (!File.Exists(sqlite)) sqlite = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
    string hostSqlite = Path.Combine(Path.GetDirectoryName(hostPath)!, "e_sqlite3.dll");
    if (!File.Exists(hostSqlite)) File.Copy(sqlite, hostSqlite);
    var plan = new GameWatch(game.Id, game.StartTime.ToUniversalTime().Ticks, "fixture", session,
        game.StartTime.ToUniversalTime().Ticks, [new("current.log", fresh, true, false), new("old.log", old, true, false), new("cfg.ini", config, false, false)], [], [], database, [], true);
    var rejected = await Send(new() { Command = "track", Game = plan with { StartTicks = 1 } });
    Check(!rejected.Success, "reject recycled game PID");
    Check((await Send(new() { Command = "track", Game = plan })).Success, "retain exact game process handle");
    Check((await Send(new() { Command = "lookup", Game = plan })).Game?.SessionDirectory == session, "preserve original session during GUI return");
    Check((await Send(new() { Command = "transfer", Game = plan })).Success, "explicitly transfer session collector ownership");
    Check((await Send(new() { Command = "hello", OwnerPid = owner.Id, OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks,
        GuiPath = Environment.ProcessPath, UserData = output })).Success, "allow GUI to cancel parking after transfer");
    await Task.Delay(1500);
    Check(File.Exists(Path.Combine(session, "current.log")), "background snapshots continue after canceled GUI parking");
    var park = await Send(new() { Command = "park", OwnerPid = owner.Id, OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks });
    Check(park.Success, "acknowledge monitoring before GUI owner exits");
    await ExitChild(owner);
    for (int i = 0; i < 24; i++)
    {
        await Task.Delay(250);
        var memory = await Send(new() { Command = "status" });
        peakPrivate = Math.Max(peakPrivate, memory.PrivateBytes); peakWorking = Math.Max(peakWorking, memory.WorkingSetBytes);
    }
    Check(!host.HasExited, "host survives complete GUI owner exit");
    Check(peakPrivate < 40L * 1024 * 1024 && peakWorking < 40L * 1024 * 1024, "native private memory and working set remain below 40 MiB");
    Check(File.ReadAllText(Path.Combine(session, "current.log")) == "CURRENT_SESSION_SENTINEL", "background captures current session logs");
    Check(!File.Exists(Path.Combine(session, "old.log")), "background excludes stale prior-session logs");
    Check(File.ReadAllText(Path.Combine(session, "cfg.ini")).Contains("Sentinel=fixture"), "background preserves complete configuration snapshot");
    await ExitChild(game); await Task.Delay(1500);
    Check(!(await Send(new() { Command = "lookup", Game = plan })).Success, "game exit retires its exact monitor");
    await Task.Delay(2000);
    string backgroundLog = Path.Combine(output, "log", "F6Launcher-background.log");
    Check(!host.HasExited, "parked tray survives the last game exit");
    Check(!File.ReadAllText(backgroundLog).Contains("fixture restore requested"),
        "last game exit never requests GUI restoration");
    using (var returningOwner = Process.GetCurrentProcess())
    {
        var resumed = await Send(new() { Command = "hello", OwnerPid = returningOwner.Id,
            OwnerStartTicks = returningOwner.StartTime.ToUniversalTime().Ticks,
            GuiPath = Environment.ProcessPath, UserData = output });
        Check(resumed.Success && resumed.Game is null && resumed.Games is { Length: 0 },
            "manually reopened GUI inherits no stale game after the last process exits");
    }
    using (var connection = new SqliteConnection($"Data Source={database}"))
    {
        connection.Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT COUNT(DISTINCT State) FROM PlayTimeItem WHERE State IN (1,2,3);";
        Check(Convert.ToInt32(cmd.ExecuteScalar()) == 3, "native playtime records start, heartbeat and stop without managed child");
    }
    string cache = Path.Combine(output, "dll-components.json");
    File.WriteAllText(cache, "{\"dlss\":[{\"Family\":\"dlss\",\"Version\":\"999.1\",\"Url\":\"https://example.invalid/dlss.zip\"}]}");
    var cached = DllComponentCatalog.LoadCached(cache);
    Check(cached.Of("dlss").Any(c => c.Version == "999.1"), "DLL page reads cached remote versions offline");
    Check(cached.Of("dlssnr").Count > 0, "local NR catalog enriches the cached manifest");
    File.WriteAllText(cache, "broken fixture");
    Check(DllComponentCatalog.LoadCached(cache).Of("dlssnr").Count > 0, "corrupt manifest retains bundled NR choices");

    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var bodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var responseTask = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(); using var stream = socket.GetStream();
        var buffer = new byte[4096]; await stream.ReadAsync(buffer);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\n"));
        await stream.FlushAsync(); await bodyGate.Task;
        await stream.WriteAsync(Encoding.ASCII.GetBytes("TEST"));
    });
    using var http = new HttpClient(DohService.CreateSocketsHttpHandler()) { DefaultRequestVersion = HttpVersion.Version11 };
    string url = $"http://127.0.0.1:{port}/plugins.json?authKey=SECRET_SENTINEL";
    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    Check(NetworkRequestActivity.Snapshot().Any(r => r.Active && r.Name == "插件目录"), "request list stays active while body downloads");
    Check(NetworkRequestActivity.Snapshot().All(r => !r.Endpoint.Contains("SECRET_SENTINEL")), "request list omits query credentials");
    bodyGate.SetResult();
    Check(await response.Content.ReadAsStringAsync() == "TEST", "HTTP activity preserves response bytes");
    await responseTask;
    Check(NetworkRequestActivity.Snapshot().Any(r => !r.Active && r.Name == "插件目录" && r.Status == "完成"), "request completion updates the real-time history");
    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = passed, PeakPrivateBytes = peakPrivate, PeakWorkingSetBytes = peakWorking, LoadedModules = modules, RealGameTested = false, RealUiTested = false }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS {passed}; native peak private={peakPrivate / 1048576d:F2} MiB, working set={peakWorking / 1048576d:F2} MiB; no game or GUI started");
    return 0;
}
finally
{
    try { if (!host.HasExited) await Send(new() { Command = "exit" }); } catch { }
    await ExitChild(owner); await ExitChild(game);
    if (!host.HasExited) await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
}
