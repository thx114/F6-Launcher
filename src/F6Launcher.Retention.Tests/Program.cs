using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HoYoShadeHub.Features.Update;
using HoYoShadeHub.Helpers;

if (args.SequenceEqual(new[] { "--hold" }))
{
    Console.WriteLine("RETENTION_FIXTURE_READY");
    Console.ReadLine();
    return;
}
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run the NTFS and process integration checks on Windows.");
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
PortableAppProcessSnapshot Empty() => new(Array.Empty<string>());

// Existing suffix-named builds, real NTFS hardlinks, user data, and stable rollback across restarts.
using (var fixture = new Fixture())
{
    string old = fixture.App("1.4.8.1", 1);
    string running = fixture.App("1.4.8.2-bg-light-20261115", 2);
    string rollback = fixture.App("1.4.8.2-getgames-20261115", 3);
    string current = fixture.App("1.4.8.2-retention-20261116", 4);
    fixture.Select(current);
    string shared = Path.Combine(current, "shared.bin");
    File.WriteAllText(shared, "shared payload must remain unchanged");
    Fixture.Link(Path.Combine(old, "shared.bin"), shared);
    byte[] before = SHA256.HashData(File.ReadAllBytes(shared));
    FileAttributes attributes = File.GetAttributes(shared);
    var guards = fixture.UserData();
    PortableAppProcessSnapshot Processes() => new(new[] { Path.Combine(running, "F6Launcher.BackgroundHost.exe") });
    var result = PortableAppCleanupPolicy.Run(fixture.Root, current, Processes);
    Check(result.SkippedReason is null, "A selected successful build must clean up.");
    Check(result.RollbackDirectory == Path.GetFileName(rollback), "Migration must use deployment directory time, not hardlinked EXE time.");
    Check(!Directory.Exists(old) && result.RemovedDirectories.Count == 1, "Only the obsolete payload should be removed.");
    Check(Directory.Exists(current) && Directory.Exists(rollback) && Directory.Exists(running), "Current, rollback and active host must survive.");
    Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(shared))) && File.GetAttributes(shared) == attributes,
        "Removing an old hardlink must not change shared content or attributes.");
    Check(guards.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "Game profiles, backgrounds, catalogs and settings must remain unchanged.");
    string laterTouch = fixture.App("1.4.8.2-other", 10);
    result = PortableAppCleanupPolicy.Run(fixture.Root, current, Processes);
    Check(result.RollbackDirectory == Path.GetFileName(rollback) && !Directory.Exists(laterTouch), "Restarts must preserve the remembered rollback.");
    result = PortableAppCleanupPolicy.Run(fixture.Root, current, Empty);
    Check(!Directory.Exists(running), "A stopped helper's old directory must become eligible on the next startup.");
    string next = fixture.App("1.4.8.2-next", 11);
    fixture.Select(next);
    result = PortableAppCleanupPolicy.Run(fixture.Root, next, Empty);
    Check(result.RollbackDirectory == Path.GetFileName(current) && Directory.Exists(current), "A successful new build must rotate the former current into rollback.");
    Check(!Directory.Exists(rollback), "Only one rollback should remain after rotation.");
}

// Failed/unloaded startup and an old GUI after an update must never retire the selected new payload.
using (var fixture = new Fixture())
{
    string old = fixture.App("1.4.8.1", 1), current = fixture.App("1.4.8.2", 2), future = fixture.App("1.4.8.2-new", 3);
    fixture.Select(future);
    var result = PortableAppCleanupPolicy.Run(fixture.Root, current, Empty);
    Check(result.SkippedReason is not null && Directory.Exists(old) && Directory.Exists(future), "Old GUI must not clean after the selection changes.");
    Check(!File.Exists(fixture.StatePath), "An unselected build must not be recorded as successful.");
    result = PortableAppCleanupPolicy.Run(fixture.Root, Path.GetDirectoryName(fixture.Root)!, Empty);
    Check(result.SkippedReason is not null, "Directories outside the portable installation must be rejected.");
    File.Delete(Path.Combine(future, "F6Launcher.runtimeconfig.json"));
    result = PortableAppCleanupPolicy.Run(fixture.Root, future, Empty);
    Check(result.SkippedReason is not null && !File.Exists(fixture.StatePath), "An incomplete selected payload must not be treated as a successful startup.");
}

// The same lease used by installation covers cleanup, with cancellation while waiting.
using (var fixture = new Fixture())
{
    string old = fixture.App("1.4.8.1", 1), rollback = fixture.App("1.4.8.2-before", 2), current = fixture.App("1.4.8.2", 3);
    fixture.Select(current);
    using (FileStream lease = PortableUpdatePolicy.TryAcquireMaintenance(fixture.Root)!)
    {
        var result = PortableAppCleanupPolicy.Run(fixture.Root, current, Empty);
        Check(result.SkippedReason is not null && Directory.Exists(old) && !File.Exists(fixture.StatePath), "An active installation must defer cleanup without side effects.");
        using var cancellation = new CancellationTokenSource(100);
        try
        {
            using var unexpected = await PortableUpdatePolicy.AcquireMaintenanceAsync(fixture.Root, cancellation.Token);
            Check(false, "A competing installer must not obtain the lease.");
        }
        catch (OperationCanceledException) { Check(true, "Lease wait must honor cancellation."); }
    }
    using (var lease = await PortableUpdatePolicy.AcquireMaintenanceAsync(fixture.Root, default))
        Check(lease is not null, "Closing a maintenance lease must release it.");
    var cleaned = PortableAppCleanupPolicy.Run(fixture.Root, current, Empty);
    Check(cleaned.RemovedDirectories.Count == 1 && !Directory.Exists(old) && Directory.Exists(rollback), "A later startup must complete deferred cleanup.");
}

// An inaccessible launcher path or a pointer switch during process discovery must defer deletion.
using (var fixture = new Fixture())
{
    string old = fixture.App("1.4.8.1", 1), rollback = fixture.App("1.4.8.2-before", 2), current = fixture.App("1.4.8.2", 3);
    fixture.Select(current);
    var result = PortableAppCleanupPolicy.Run(fixture.Root, current, () => new(Array.Empty<string>(), Complete: false));
    Check(result.SkippedReason is not null && Directory.Exists(old) && !File.Exists(fixture.StatePath), "Unknown launcher process identity must defer cleanup.");
    int reads = 0;
    result = PortableAppCleanupPolicy.Run(fixture.Root, current, () =>
    {
        if (++reads == 2) fixture.Select(old);
        return Empty();
    });
    Check(result.SkippedReason is not null && result.RemovedDirectories.Count == 0 && Directory.Exists(old), "A pointer change during discovery must stop deletion.");
    Check(File.ReadAllText(Path.Combine(fixture.Root, "version.ini")).Contains(Path.GetFileName(old)), "Cleanup must never rewrite the activation pointer.");
    fixture.Select(current);
    reads = 0;
    result = PortableAppCleanupPolicy.Run(fixture.Root, current, () => ++reads == 1 ? Empty() : new(new[] { Path.Combine(old, "HoYoShadeHub.RPC.exe") }));
    Check(Directory.Exists(old) && result.KeptDirectories.Contains(Path.GetFileName(old)), "A newly observed old helper must be protected before removal.");
}

// Interrupted removal must remain retryable; do not change attributes on a read-only shared hardlink.
using (var fixture = new Fixture())
{
    string old = fixture.App("1.4.8.1", 1), rollback = fixture.App("1.4.8.2-before", 2), current = fixture.App("1.4.8.2", 3);
    fixture.Select(current);
    string shared = Path.Combine(current, "shared-readonly.bin");
    File.WriteAllText(shared, "keep shared readonly bytes");
    Fixture.Link(Path.Combine(old, "z-readonly.bin"), shared);
    File.SetAttributes(shared, FileAttributes.ReadOnly);
    var result = PortableAppCleanupPolicy.Run(fixture.Root, current, Empty);
    Check(result.DeferredDirectories.Count == 1 && Directory.Exists(old), "Locked or read-only payloads must be left for retry.");
    Check(File.GetAttributes(shared).HasFlag(FileAttributes.ReadOnly) && File.ReadAllText(shared) == "keep shared readonly bytes",
        "Cleanup must not clear attributes on shared links.");
    File.Delete(Path.Combine(old, "F6Launcher.exe"));
    File.Delete(Path.Combine(old, "F6Launcher.dll"));
    Check(File.ReadAllText(fixture.StatePath).Contains(Path.GetFileName(old)), "Pending retirement must be saved before destructive work.");
    File.SetAttributes(shared, FileAttributes.Normal); // Only this fixture's own hardlink.
    result = PortableAppCleanupPolicy.Run(fixture.Root, current, Empty);
    Check(!Directory.Exists(old) && result.RemovedDirectories.Count == 1, "An incomplete payload recorded as pending must be retried.");
    Check(File.ReadAllText(shared) == "keep shared readonly bytes", "Retry must preserve the retained hardlink.");
}

// Incomplete installations, unrelated folders, root junctions and nested junctions are never traversed.
using (var fixture = new Fixture())
using (var outside = new Fixture())
{
    string old = fixture.App("1.4.8.1", 1), rollback = fixture.App("1.4.8.2-before", 2), current = fixture.App("1.4.8.2", 3);
    fixture.Select(current);
    string unrelated = Path.Combine(fixture.Root, "app-data"), incomplete = Path.Combine(fixture.Root, "app-1.4.9-incomplete");
    Directory.CreateDirectory(unrelated); Directory.CreateDirectory(incomplete);
    File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "unrelated");
    File.WriteAllText(Path.Combine(incomplete, "F6Launcher.exe"), "unfinished");
    string external = outside.App("1.4.7", 1);
    string rootLink = Path.Combine(fixture.Root, "app-1.4.7-linked");
    Fixture.Junction(rootLink, external);
    Fixture.Junction(Path.Combine(old, "external"), external);
    var result = PortableAppCleanupPolicy.Run(fixture.Root, current, Empty);
    Check(Directory.Exists(unrelated) && Directory.Exists(incomplete), "Unrelated and unfinished directories must remain untouched.");
    Check(File.ReadAllText(Path.Combine(external, "F6Launcher.dll")) == "fixture assembly", "External junction targets must remain intact.");
    Check(Directory.Exists(old) && File.Exists(Path.Combine(old, "F6Launcher.dll")), "A nested junction must defer the whole candidate before any file is removed.");
    Check(result.DeferredDirectories.Count == 2, "Root and nested reparse candidates must both be reported as deferred.");
    Directory.Delete(rootLink); Directory.Delete(Path.Combine(old, "external"));
    result = PortableAppCleanupPolicy.Run(fixture.Root, current, Empty);
    Check(!Directory.Exists(old), "A candidate may be retired after its junction is removed.");
}

// Corrupted or escaping state must not authorize arbitrary deletions.
using (var fixture = new Fixture())
{
    string old = fixture.App("1.4.8.1", 1), rollback = fixture.App("1.4.8.2-before", 2), current = fixture.App("1.4.8.2", 3);
    fixture.Select(current);
    Directory.CreateDirectory(Path.GetDirectoryName(fixture.StatePath)!);
    File.WriteAllText(fixture.StatePath, "{\"Format\":1,\"Current\":\"app-1.4.8.2\",\"Pending\":[\"../outside\"]}");
    try { PortableAppCleanupPolicy.Run(fixture.Root, current, Empty); Check(false, "Escaping state must be rejected."); }
    catch (InvalidDataException) { Check(Directory.Exists(old), "Rejected state must not remove files."); }
    File.WriteAllText(fixture.StatePath, "invalid-json");
    try { PortableAppCleanupPolicy.Run(fixture.Root, current, Empty); Check(false, "Corrupt state must not reset retention implicitly."); }
    catch (JsonException) { Check(Directory.Exists(old), "Corrupt state must defer cleanup."); }
}

// Discover and retain a real native-image helper path, then release it without touching any game/GUI.
using (var fixture = new Fixture())
{
    string old = fixture.App("1.4.8.1-active", 1), rollback = fixture.App("1.4.8.2-before", 2), current = fixture.App("1.4.8.2", 3);
    fixture.Select(current);
    string assembly = typeof(Fixture).Assembly.Location;
    string output = Path.GetDirectoryName(assembly)!;
    foreach (string suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        File.Copy(Path.ChangeExtension(assembly, null) + suffix, Path.Combine(old, Path.GetFileNameWithoutExtension(assembly) + suffix));
    File.Copy(Path.Combine(output, "Microsoft.Extensions.Logging.Abstractions.dll"), Path.Combine(old, "Microsoft.Extensions.Logging.Abstractions.dll"));
    string image = Path.Combine(old, "F6Launcher.BackgroundHost.exe");
    File.Copy(Path.ChangeExtension(assembly, ".exe"), image);
    var info = new ProcessStartInfo(image) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
    info.ArgumentList.Add("--hold");
    using Process helper = Process.Start(info)!;
    try
    {
        string? ready = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Check(ready == "RETENTION_FIXTURE_READY", "The controlled helper fixture must start.");
        var snapshot = PortableAppCleanupService.ReadProcesses();
        Check(snapshot.Complete && snapshot.ImagePaths.Contains(image, StringComparer.OrdinalIgnoreCase), "Limited-access native process discovery must find the real helper image.");
        var result = PortableAppCleanupPolicy.Run(fixture.Root, current, PortableAppCleanupService.ReadProcesses);
        Check(result.SkippedReason is null && Directory.Exists(old), "A real running helper must keep its application directory.");
    }
    finally
    {
        helper.StandardInput.Close();
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
    var cleaned = PortableAppCleanupPolicy.Run(fixture.Root, current, PortableAppCleanupService.ReadProcesses);
    Check(cleaned.SkippedReason is null && !Directory.Exists(old), "The old helper payload must be removable after its controlled exit.");
}

Console.WriteLine($"Portable app retention: PASS {checks}; nativeProcessFixture=true; hardlinksVerified=true; gameTested=false; uiTested=false.");

sealed class Fixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "f6-retention-fixture-" + Guid.NewGuid().ToString("N"));
    internal string StatePath => Path.Combine(Root, PortableAppCleanupPolicy.StateRelativePath.Replace('/', Path.DirectorySeparatorChar));
    internal Fixture() => Directory.CreateDirectory(Root);
    internal string App(string suffix, int order)
    {
        string path = Path.Combine(Root, "app-" + suffix);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "F6Launcher.exe"), "fixture executable");
        File.WriteAllText(Path.Combine(path, "F6Launcher.dll"), "fixture assembly");
        File.WriteAllText(Path.Combine(path, "F6Launcher.runtimeconfig.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(path, "F6Launcher.exe"), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetCreationTimeUtc(path, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(order));
        return path;
    }
    internal void Select(string app) => File.WriteAllText(Path.Combine(Root, "version.ini"), "exe_path=" + Path.GetFileName(app) + "\\F6Launcher.exe\r\n");
    internal Dictionary<string, byte[]> UserData()
    {
        var values = new Dictionary<string, byte[]>();
        foreach (string relative in new[] { "config.ini", "HoYoShadeHubDatabase.db", "OptiScaler/profiles/hk4e_cn.ini", "bg/background.mp4", ".hysx/catalog/plugins.json" })
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            values[path] = System.Text.Encoding.UTF8.GetBytes("user data " + relative);
            File.WriteAllBytes(path, values[path]);
        }
        return values;
    }
    internal static void Link(string link, string existing)
    {
        if (!CreateHardLinkW(link, existing, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void Junction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Cannot create test junction: " + output);
    }
    public void Dispose() => Remove(Root);
    private static void Remove(string directory)
    {
        if (!Path.GetFileName(directory).StartsWith("f6-retention-fixture-", StringComparison.Ordinal) && !directory.Contains(Path.DirectorySeparatorChar + "f6-retention-fixture-"))
            throw new InvalidOperationException("Refusing cleanup outside the fixture.");
        foreach (string child in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(child);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(child);
                else Remove(child);
            }
            else
            {
                // Fixture-only disposal. Production cleanup deliberately never clears shared attributes.
                if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(child, attributes & ~FileAttributes.ReadOnly);
                File.Delete(child);
            }
        }
        Directory.Delete(directory);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
}
