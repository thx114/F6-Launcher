using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PackConsentMigrationTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            passed++;
        }
        string hash = new('a', 64);
        string[] names = ["A|B", "name:colon", "quote\"", "\u539f\u795e"];
        var parsed = PackConsentRecord.Parse(PackConsentRecord.Serialize(hash, names));
        Check(parsed is not null && parsed.Hash == hash && parsed.Disabled.SetEquals(names), "Names round trip exactly");
        Check(PackConsentRecord.Parse("{\"schema\":1,\"hash\":\"" + hash + "\"}") is null, "Missing disabled blocks");
        Check(PackConsentRecord.Parse("{\"schema\":1,\"hash\":\"" + hash + "\",\"disabled\":[\"a\",\"a\"]}") is null, "Duplicate names block");
        Check(PackConsentRecord.Parse("{\"schema\":1,\"hash\":\"bad\",\"disabled\":[]}") is null, "Malformed hash blocks");
        Check(PackConsentRecord.ParseLegacy(hash, null) is null, "Missing legacy disabled blocks");
        Check(PackConsentRecord.ParseLegacy(hash, new string('b', 64) + ":") is null, "Legacy hash mismatch blocks");
        Check(PackConsentRecord.ParseLegacy(hash, hash + ":A|B") is null, "Ambiguous legacy delimiters block");
        Check(PackConsentRecord.ParseLegacy(hash, hash + ":A")?.Disabled.SetEquals(["A"]) == true, "Complete single legacy name remains compatible");
        Check(PackConsentRecord.ParseLegacy(hash, hash + ":")?.Disabled.Count == 0, "Complete legacy empty set remains compatible");

        string root = Path.Combine(Path.GetTempPath(), "hys-pack-consent-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "pending"), target = Path.Combine(root, "target");
            Directory.CreateDirectory(Path.Combine(source, "presets", "nested"));
            Directory.CreateDirectory(Path.Combine(source, "game_files", "empty"));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "auto.json"), "actions");
            File.WriteAllText(Path.Combine(source, "presets", "nested", "test.ini"), "preset");
            File.WriteAllText(Path.Combine(target, "auto.json"), "old");
            File.WriteAllText(Path.Combine(target, PackOnceLedger.FileName), "existing ledger");
            string[] originalFiles = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(target, path)).Order(StringComparer.Ordinal).ToArray();
            using (var held = new FileStream(Path.Combine(target, "auto.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!PackPendingMigration.TryMigrate(source, target, out _) && Directory.Exists(source)
                    && File.Exists(Path.Combine(source, "presets", "nested", "test.ini")), "Copy failure retains full source");
            Check(File.ReadAllText(Path.Combine(target, "auto.json")) == "old"
                && File.ReadAllText(Path.Combine(target, PackOnceLedger.FileName)) == "existing ledger"
                && originalFiles.SequenceEqual(Directory.GetFiles(target, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(target, path)).Order(StringComparer.Ordinal)),
                "Failed migration preserves original destination bytes and file set");
            Check(PackPendingMigration.TryMigrate(source, target, out _) && !Directory.Exists(source), "Complete migration removes pending source");
            Check(File.ReadAllText(Path.Combine(target, "presets", "nested", "test.ini")) == "preset"
                && Directory.Exists(Path.Combine(target, "game_files", "empty")), "Recursive files and empty directories preserved");
            Check(File.ReadAllText(Path.Combine(target, PackOnceLedger.FileName)) == "existing ledger", "Existing once ledger preserved");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, PackOnceLedger.FileName), "replacement");
            Check(!PackPendingMigration.TryMigrate(source, target, out _) && Directory.Exists(source)
                && File.ReadAllText(Path.Combine(target, PackOnceLedger.FileName)) == "existing ledger", "Pending cannot overwrite once history");

            string cache = Path.Combine(root, "cache");
            string pending = Path.Combine(cache, "games", ".pending-gamepacks", "hk4e");
            string gamePack = GameAddonPack.PackDirectory(cache, "hk4e_cn");
            Directory.CreateDirectory(Path.Combine(pending, "NR"));
            File.WriteAllText(Path.Combine(pending, "auto.json"), "new actions");
            byte[] nr = [0, 1, 2, 3, 255]; // File transport fixture; never loaded as a DLL.
            File.WriteAllBytes(Path.Combine(pending, "NR", "nvngx_dlssnr.dll"), nr);
            Check(PackPendingMigration.PlaceMatching(cache, "nap_cn") == 0 && Directory.Exists(pending),
                "Unrelated game does not consume pending F5 payload");
            Directory.CreateDirectory(gamePack);
            File.WriteAllText(Path.Combine(gamePack, "auto.json"), "old actions");
            using (var held = new FileStream(Path.Combine(gamePack, "auto.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool blocked = false;
                try { PackPendingMigration.PlaceMatching(cache, "hk4e_cn"); }
                catch (IOException) { blocked = true; }
                Check(blocked && File.Exists(Path.Combine(pending, "NR", "nvngx_dlssnr.dll")),
                    "Shared import placement fails closed and retains nested NR source");
            }
            Check(PackPendingMigration.PlaceMatching(cache, "hk4e_cn") == 1 && !Directory.Exists(pending),
                "First-import shared placement completes before removing pending source");
            Check(File.ReadAllBytes(Path.Combine(gamePack, "NR", "nvngx_dlssnr.dll")).SequenceEqual(nr)
                && File.ReadAllText(Path.Combine(gamePack, "auto.json")) == "new actions",
                "First-import placement retains NR subdirectory and action file together");
            Check(PackPendingMigration.PlaceMatching(cache, "hk4e_cn") == 0,
                "Completed first-import placement is idempotent");
        }
        finally { Directory.Delete(root, recursive: true); }
        return passed;
    }
}
