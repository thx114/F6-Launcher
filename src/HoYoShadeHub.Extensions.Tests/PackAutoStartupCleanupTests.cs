using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PackAutoStartupCleanupTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "auto-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int passed = 0;
            void Check(bool ok, string name)
            {
                if (!ok) throw new Exception(name);
                passed++;
                Console.WriteLine("PASS " + name);
            }
            string games = Path.Combine(root, "cache", "games");
            string pack = Path.Combine(games, "hk4e_cn");
            string pending = Path.Combine(games, ".pending-gamepacks", "nap");
            Directory.CreateDirectory(pack);
            Directory.CreateDirectory(pending);
            File.WriteAllText(Path.Combine(pack, "auto.json"), "old");
            File.WriteAllText(Path.Combine(pending, "auto.json"), "pending-old");
            string ini = Path.Combine(pack, "ini_config.json");
            File.WriteAllText(ini, "keep");
            string source = Path.Combine(root, "incoming-overlay");
            Directory.CreateDirectory(source);
            string incoming = Path.Combine(source, "auto.json");
            File.WriteAllText(incoming, "{\"actions\":[]}");
            Check(PackAutoStartupCleanup.DeleteExisting(games, games) == 0
                && File.Exists(Path.Combine(pack, "auto.json")) && File.Exists(Path.Combine(pending, "auto.json")),
                "startup preserves both installed and never-applied pending actions");
            Check(File.ReadAllText(ini) == "keep", "other overlay configuration preserved");
            Check(File.Exists(incoming), "incoming overlay source untouched");
            File.Copy(incoming, Path.Combine(pack, "auto.json"), overwrite: true);
            Check(File.ReadAllText(Path.Combine(pack, "auto.json")) == File.ReadAllText(incoming), "overlay installed after startup remains available");
            using (var held = new FileStream(Path.Combine(pack, "auto.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check(PackAutoStartupCleanup.DeleteExisting(games) == 0,
                    "startup does not open or delete even exclusively locked action files");
            }
            Check(PackAutoStartupCleanup.DeleteExisting(Path.Combine(root, "missing")) == 0, "missing roots are harmless");
            File.Delete(Path.Combine(pack, "auto.json"));
            string hidden = Path.Combine(pack, "nested", "hidden");
            Directory.CreateDirectory(hidden);
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden | FileAttributes.System);
            string mixedCase = Path.Combine(hidden, "AUTO.JsOn");
            File.WriteAllText(mixedCase, "old-hidden");
            File.SetAttributes(mixedCase, FileAttributes.Hidden | FileAttributes.System);
            string backup = Path.Combine(hidden, "auto.json.backup");
            File.WriteAllText(backup, "keep");
            Check(PackAutoStartupCleanup.DeleteExisting(games) == 0 && File.Exists(mixedCase),
                "deep hidden and system actions are neither enumerated nor removed");
            Check(File.ReadAllText(backup) == "keep", "similar names remain untouched");

            string directoryLink = Path.Combine(pack, "linked-content");
            string fileLink = Path.Combine(pack, "auto.json");
            try
            {
                Directory.CreateSymbolicLink(directoryLink, source);
                File.CreateSymbolicLink(fileLink, incoming);
                Check(PackAutoStartupCleanup.DeleteExisting(directoryLink) == 0 && File.Exists(incoming),
                    "linked root is not traversed");
                Check(PackAutoStartupCleanup.DeleteExisting(games) == 0 && File.Exists(incoming) && File.Exists(fileLink),
                    "linked child directory and action file are excluded");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("SKIP symbolic-link fixtures: Windows symlink privilege unavailable");
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 1314)
            {
                Console.WriteLine("SKIP symbolic-link fixtures: Windows symlink privilege unavailable");
            }
            Console.WriteLine($"Startup auto cleanup: PASS {passed}, FAIL 0");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
