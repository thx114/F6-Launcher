global using HoYoShadeHub.Language;
using System.Diagnostics;
using HoYoShadeHub.Features.GameLauncher;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length != 2) throw new ArgumentException("Usage: <native injector binary> <existing framework root>");
string native = Path.GetFullPath(args[0]);
string realRoot = Path.GetFullPath(args[1]);
File.Copy(native, Path.Combine(AppContext.BaseDirectory, "F6ShadeInjector.exe"), overwrite: true);
string fixture = Path.Combine(Path.GetTempPath(), "F6-injector-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
int checks = 0;
void Check(bool value, string description)
{
    if (!value) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
    checks++;
}

async Task<(int code, string stdout, string stderr)> RunAsync(params string[] arguments)
{
    var start = new ProcessStartInfo(native)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var child = Process.Start(start)!;
    Task<string> stdout = child.StandardOutput.ReadToEndAsync();
    Task<string> stderr = child.StandardError.ReadToEndAsync();
    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    finally
    {
        if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
    }
    return (child.ExitCode, await stdout, await stderr);
}

try
{
    var selection = await RunAsync("--self-test");
    Check(selection.code == 0, "native selection excludes zero-thread records and foreign sessions");
    string realIni = Path.Combine(realRoot, "ReShade.ini");
    byte[] original = File.ReadAllBytes(realIni);
    var diagnostic = await RunAsync("YuanShen.exe", "--shade-root", realRoot, "--check-ready");
    Check(diagnostic.code == 0 && diagnostic.stderr.Contains(InjectorHelper.READY_MARKER),
        "real framework emits READY in diagnostic mode with existing YuanShen records");
    Check(original.AsSpan().SequenceEqual(File.ReadAllBytes(realIni)),
        "diagnostic mode preserves real framework INI bytes");

    foreach (string dir in new[] { "InjectResource/Fonts", "LauncherResource", "reshade-shaders/Addons", "Presets" })
        Directory.CreateDirectory(Path.Combine(fixture, dir));
    foreach (string file in new[] { "inject.exe", "ReShade64.dll", "InjectResource/Fonts/MiSans-Bold.ttf" })
        File.WriteAllText(Path.Combine(fixture, file), "fixture: never loaded into a process");
    File.Copy(Path.Combine(realRoot, "LauncherResource/INIBuild.exe"), Path.Combine(fixture, "LauncherResource/INIBuild.exe"));
    string target = "F6NeverStarted_" + Guid.NewGuid().ToString("N") + ".exe";
    var clock = Stopwatch.StartNew();
    var ready = await InjectorHelper.StartAndWaitForReadyAsync(Path.Combine(fixture, "inject.exe"),
        target, fixture, NullLogger.Instance, "HoYoShade fixture", timeoutMs: 8000);
    using var owned = ready.process;
    try
    {
        Check(ready.success && ready.exitCode == InjectorErrorCodes.INJECTION_READY && owned is not null,
            "production InjectorHelper receives READY from the launcher-owned injector");
        await Task.Delay(250);
        Check(!owned!.HasExited, "ready injector stays armed while the unique target is absent");
        Check(File.Exists(Path.Combine(fixture, "ReShade.ini")),
            "normal startup completes isolated INIBuild before handing off");
        Check(clock.ElapsedMilliseconds < 8000, "ready handoff completes without the 30-second stall");
        Console.WriteLine($"INFO readiness elapsed: {clock.ElapsedMilliseconds} ms");
    }
    finally
    {
        if (owned is not null && !owned.HasExited) { owned.Kill(); await owned.WaitForExitAsync(); }
    }

    File.Delete(Path.Combine(fixture, "ReShade64.dll"));
    var invalid = await InjectorHelper.StartAndWaitForReadyAsync(Path.Combine(fixture, "inject.exe"),
        target, fixture, NullLogger.Instance, "incomplete fixture", timeoutMs: 3000);
    Check(!invalid.success && invalid.process is null && invalid.exitCode == InjectorErrorCodes.INJECTION_ERROR_FILE_INTEGRITY,
        "missing framework file returns integrity failure rather than granting readiness");
    Check(original.AsSpan().SequenceEqual(File.ReadAllBytes(realIni)), "all isolated checks preserve the real INI");
    Console.WriteLine($"PASS {checks} injector checks; no game launched or injected");
}
finally
{
    // This unique temporary fixture contains only files created/copied by this test.
    Directory.Delete(fixture, recursive: true);
}
