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
            void Check(bool ok, string name)
            {
                if (!ok) throw new Exception(name);
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
            Check(PackAutoStartupCleanup.DeleteExisting(games, games) == 2, "existing game and pending actions removed once");
            Check(File.ReadAllText(ini) == "keep", "other overlay configuration preserved");
            Check(File.Exists(incoming), "incoming overlay source untouched");
            File.Copy(incoming, Path.Combine(pack, "auto.json"));
            Check(File.ReadAllText(Path.Combine(pack, "auto.json")) == File.ReadAllText(incoming), "overlay installed after startup remains available");
            using (var held = new FileStream(Path.Combine(pack, "auto.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool blocked = false;
                try { PackAutoStartupCleanup.DeleteExisting(games); }
                catch (IOException) { blocked = true; }
                Check(blocked, "locked legacy action fails startup cleanup explicitly");
            }
            Check(PackAutoStartupCleanup.DeleteExisting(Path.Combine(root, "missing")) == 0, "missing roots are harmless");
            Console.WriteLine("Startup auto cleanup: PASS 6, FAIL 0");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
