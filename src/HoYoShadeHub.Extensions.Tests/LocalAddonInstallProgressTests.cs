using System.IO.Compression;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class LocalAddonInstallProgressTests
{
    public static async Task RunAsync(string testRoot)
    {
        string root = Path.Combine(testRoot, "addon-progress");
        string addons = Path.Combine(root, "Addons"), opti = Path.Combine(root, "opti"), cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(addons);
        Directory.CreateDirectory(opti);
        string source = Path.Combine(root, "fixture.addon64");
        byte[] payload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(source, payload);
        var installer = new LocalPackageInstaller(opti, addons, Path.Combine(root, "modules"), cacheRoot: cache);
        var reports = new List<LocalInstallProgress>();
        var progress = new LocalInstallProgressReporter(reports.Add);

        // An addon import must neither read nor mutate unrelated NR libraries.
        string nr = Path.Combine(opti, "nvngx_dlssnr.dll");
        await File.WriteAllTextAsync(nr, "unrelated NR");
        using (File.Open(nr, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await installer.InstallAsync(source, progress: progress).WaitAsync(TimeSpan.FromSeconds(15));
            Check(result.Kind == LocalPackageKind.Addon, "Wrong bare-addon route");
        }
        Check(!Directory.Exists(cache), "Addon installation touched the NR cache");
        string target = Path.Combine(addons, Path.GetFileName(source));
        Check(File.ReadAllBytes(target).SequenceEqual(payload), "Bare addon payload differs");
        Check(reports.Any(p => p.Percent is > 30 and < 80), "Byte progress missing");
        VerifyProgress(reports, extraction: false);

        string archived = Path.Combine(root, "old.addon64");
        HysxFileLink.LinkOrCopy(target, archived);
        Check(HysxFileLink.AreHardLinked(target, archived), "Hard-link fixture not linked");
        await File.WriteAllTextAsync(source, "new addon revision");
        reports.Clear();
        await installer.InstallAsync(source, progress: progress);
        Check(File.ReadAllBytes(archived).SequenceEqual(payload), "Replacement mutated archived addon");
        Check(File.ReadAllText(target) == "new addon revision", "Replacement did not commit");
        VerifyProgress(reports, extraction: false);

        string zipPath = Path.Combine(root, "addons.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("nested/first.addon64").Open())) writer.Write("first");
            using (var writer = new StreamWriter(zip.CreateEntry("nested/second.addon32").Open())) writer.Write("second");
        }
        reports.Clear();
        await installer.InstallAsync(zipPath, progress: progress);
        VerifyProgress(reports, extraction: true);
        Check(File.ReadAllText(Path.Combine(addons, "first.addon64")) == "first"
            && File.ReadAllText(Path.Combine(addons, "second.addon32")) == "second", "ZIP addon payload incomplete");

        // Cancel during byte copying, before the staged file replaces the old inode.
        await File.WriteAllBytesAsync(source, payload);
        reports.Clear();
        using (var cancel = new CancellationTokenSource())
        {
            var cancelProgress = new LocalInstallProgressReporter(p =>
            {
                reports.Add(p);
                if (p.Percent is > 30 and < 80) cancel.Cancel();
            });
            try
            {
                await installer.InstallAsync(source, cancellationToken: cancel.Token, progress: cancelProgress);
                throw new InvalidOperationException("Canceled copy succeeded");
            }
            catch (OperationCanceledException) { }
        }
        Check(File.ReadAllText(target) == "new addon revision", "Canceled copy changed installed addon");
        Check(!reports.Any(p => p.Percent >= 80), "Canceled copy reported completion");
        Check(!Directory.GetFiles(addons, "*.hysx-new-*").Any(), "Canceled copy leaked staging file");

        reports.Clear();
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                await installer.InstallAsync(source, progress: progress);
                throw new InvalidOperationException("Locked addon replacement succeeded");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Check(File.ReadAllText(target) == "new addon revision", "Failed replacement changed installed addon");
        Check(!reports.Any(p => p.Percent >= 80), "Failed replacement reported completion");
        Check(!Directory.GetFiles(addons, "*.hysx-new-*").Any(), "Failed replacement leaked staging file");

        string empty = Path.Combine(root, "empty.addon64");
        File.WriteAllBytes(empty, []);
        reports.Clear();
        await installer.InstallAsync(empty, progress: progress);
        VerifyProgress(reports, extraction: false);
        Check(new FileInfo(Path.Combine(addons, "empty.addon64")).Length == 0, "Zero-length copy failed");
        Console.WriteLine("PASS addon byte progress, ZIP progress, NR isolation, hardlink preservation, cancellation and failure cleanup");
    }

    private static void VerifyProgress(List<LocalInstallProgress> reports, bool extraction)
    {
        Check(reports.First().Percent is null, "Identification has a fabricated percentage");
        Check(reports.Any(p => p.Percent == 30) && reports.Any(p => p.Percent == 80)
            && reports.Last().Percent == 90, "Addon stages missing");
        if (extraction) Check(reports.Any(p => p.Percent is >= 2 and < 30), "ZIP progress missing");
        double[] values = reports.Where(p => p.Percent.HasValue).Select(p => p.Percent!.Value).ToArray();
        Check(values.All(p => double.IsFinite(p) && p >= 0 && p <= 90), "Invalid addon percentage");
        Check(values.Zip(values.Skip(1)).All(p => p.First <= p.Second), "Addon progress moved backwards");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
