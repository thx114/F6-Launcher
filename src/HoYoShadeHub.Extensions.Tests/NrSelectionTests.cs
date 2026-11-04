using System.IO.Compression;
using System.Text.Json;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class NrSelectionTests
{
    public static int Run(string[] args)
    {
        int passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL " + message);
            passed++;
            Console.WriteLine("PASS " + message);
        }
        string root = Path.Combine(Path.GetTempPath(), "nr-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "version-A", "version-B" };
            string[] installedVersions = ["version-A", "version-B", "version-C", "VERSION-A"];
            Check(NrRuntimeLibrary.SelectableVersions(installedVersions, enabled.Contains).SequenceEqual(["version-A", "version-B"]), "multiple enabled NR versions stay selectable together");
            enabled.Remove("version-A");
            Check(NrRuntimeLibrary.SelectableVersions(installedVersions, enabled.Contains).SequenceEqual(["version-B"]), "disabling one NR leaves other selectable versions enabled");
            enabled.Clear();
            Check(NrRuntimeLibrary.SelectableVersions(installedVersions, enabled.Contains).Count == 0, "all NR choices can be disabled without selecting a replacement");
            string source = Path.Combine(root, "source", NrRuntimeLibrary.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "aaaa");
            string cache = Path.Combine(root, "cache");
            var store = new DllVersionStore(cache);
            Check(store.Archive("dlssnr", "custom-A", [source]) == 1, "archive initial runtime");
            string a = store.FilePath("dlssnr", "custom-A", NrRuntimeLibrary.FileName);
            Check(HysxFileLink.AreHardLinked(source, a), "archive and source share file ID");
            Check(NrRuntimeLibrary.SelectableVersions(["custom-A"], _ => false).Count == 0
                && HysxFileLink.AreHardLinked(a, NrRuntimeLibrary.VersionFile(cache, "custom-A")),
                "disabled NR remains available to existing game selections while hidden from picker");
            string copy = Path.Combine(root, "copy", NrRuntimeLibrary.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(source, copy);
            Check(!HysxFileLink.IsUpToDate(source, copy), "equal bytes alone do not count as hardlink");
            Check(NrRuntimeLibrary.Consolidate(cache, Path.GetDirectoryName(copy)).Count == 0, "legacy copy consolidation succeeds");
            Check(HysxFileLink.AreHardLinked(a, copy), "legacy duplicate linked to canonical file");
            File.WriteAllText(source + ".new", "bbbb");
            File.Move(source + ".new", source, true);
            Check(File.ReadAllText(a) == "aaaa", "replacement does not corrupt archive alias");
            store.Archive("dlssnr", "custom-B", [source]);
            string b = store.FilePath("dlssnr", "custom-B", NrRuntimeLibrary.FileName);
            Check(!HysxFileLink.AreHardLinked(a, b), "same size different content stays separate");
            var family = new DllFamily("dlssnr", "NR", NrRuntimeLibrary.FileName, DllRequirementLevel.Required, "");
            var installed = new InstalledDll(NrRuntimeLibrary.FileName, 4, "310.9.16.0") { Path = copy };
            Check(DllVariantResolver.Identify(store, family, installed, "custom-B", 4).Variant == "custom-A", "archive hash overrides stale label even without numeric prefix");
            string build = Path.Combine(root, "build");
            Directory.CreateDirectory(build);
            string dll = Path.Combine(build, "OptiScaler.dll");
            File.WriteAllText(dll, "dll");
            File.WriteAllText(Path.Combine(build, "OptiScaler.ini"), "original-config");
            string[] excludedArtifacts =
            [
                "OptiScaler_crash_123.dmp", "OPTISCALER_CRASH_456.DMP", "OptiScaler.log",
                ".nr-present-trace-123.jsonl", ".nr-guide.json", "nr-present-capture.raw",
                "OptiScaler.dll.pending", "OptiScaler.r829.tmp", "OptiScaler.pdb", "OptiScaler.dll.bak",
                ".OptiScaler.before-menu-refresh.dll", ".build.before-render-scale.json",
                ".hub-runtime-owner", ".hub-profile-authority.json",
                "backup-cc-before-lora7/nvngx_dlssnr.dll", "BACKUP-ht-before3000/OptiScaler.dll",
                ".deploy-backup-A0E04CF1/OptiScaler.ini", "backups/previous/nvngx_dlssnr.dll",
                "profile-recovery/old/profiles/hk4e_cn.ini", "current-output-captures/frame/output.raw",
                "GenshinCapture/session/output.png", "logs/OptiScaler.txt", ".git/objects/object",
                "plugins/backup-before-upgrade/loader.dll", "plugins/loader.log",
            ];
            string[] runtimeAssets =
            [
                "plugins/loader.dll", "plugins/loader.asi", "plugins/settings.ini",
                "models/network.bin", "models/weights.onnx", "assets/noise.png",
                "OptiScaler/streamline/manifest.json", "OptiScaler/streamline/nvngx_dlssnr.dll",
                "profiles/hk4e_cn.ini", "LICENSE", "licenses/plugin.txt",
            ];
            foreach (string name in excludedArtifacts.Concat(runtimeAssets))
            {
                string path = Path.Combine(build, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture:" + name);
            }
            string gameA = NrRuntimeLibrary.PrepareOpti(cache, "gameA", "source/version", dll, "custom-A");
            string gameB = NrRuntimeLibrary.PrepareOpti(cache, "gameB", "source/version", dll, "custom-B");
            string runtimeA = Path.GetDirectoryName(gameA)!;
            Check(excludedArtifacts.All(name => !File.Exists(Path.Combine(runtimeA, name))),
                "runtime excludes crash dumps, traces, backups, captures and shared ownership state");
            Check(runtimeAssets.All(name => File.Exists(Path.Combine(runtimeA, name))),
                "runtime preserves plugins, configuration, models, component data and licenses");
            Check(HysxFileLink.AreHardLinked(Path.Combine(build, "plugins/loader.dll"), Path.Combine(runtimeA, "plugins/loader.dll")),
                "nested runtime DLL remains hardlinked");
            Check(!HysxFileLink.AreHardLinked(Path.Combine(build, "plugins/settings.ini"), Path.Combine(runtimeA, "plugins/settings.ini")),
                "nested mutable configuration stays a private copy");
            Check(HysxFileLink.AreHardLinked(a, Path.Combine(runtimeA, "OptiScaler/streamline/nvngx_dlssnr.dll")),
                "nested component NR uses the selected version");
            Check(File.ReadAllText(Path.Combine(build, "backup-cc-before-lora7/nvngx_dlssnr.dll"))
                    == "fixture:backup-cc-before-lora7/nvngx_dlssnr.dll",
                "NR binding does not traverse or rewrite archived NR backups");
            string nrA = Path.Combine(Path.GetDirectoryName(gameA)!, NrRuntimeLibrary.FileName);
            string nrB = Path.Combine(Path.GetDirectoryName(gameB)!, NrRuntimeLibrary.FileName);
            Check(HysxFileLink.AreHardLinked(a, nrA) && HysxFileLink.AreHardLinked(b, nrB), "two games load independent NR selections via hardlinks");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(gameA)!, "OptiScaler.ini"), "game-A-config");
            Check(File.ReadAllText(Path.Combine(build, "OptiScaler.ini")) == "original-config"
                && File.ReadAllText(Path.Combine(Path.GetDirectoryName(gameB)!, "OptiScaler.ini")) == "original-config", "game configuration does not mutate shared or other-game config");
            Check(!File.Exists(Path.Combine(build, NrRuntimeLibrary.FileName)), "NR selection does not alter shared build");
            NrRuntimeLibrary.PrepareOpti(cache, "gameA", "source/version", dll, "custom-B");
            Check(HysxFileLink.AreHardLinked(b, nrA) && File.ReadAllText(a) == "aaaa", "switch rebinds target and preserves old version");
            Check(HysxFileLink.AreHardLinked(b, Path.Combine(runtimeA, "OptiScaler/streamline/nvngx_dlssnr.dll"))
                    && excludedArtifacts.All(name => !File.Exists(Path.Combine(runtimeA, name))),
                "repeat preparation switches nested NR without reintroducing diagnostics");
            string existingDiagnostic = Path.Combine(runtimeA, "OptiScaler_crash_user-evidence.dmp");
            File.WriteAllText(existingDiagnostic, "existing-evidence");
            HysxFileLink.LinkNeuralRuntime(a, Path.Combine(build, NrRuntimeLibrary.FileName));
            NrRuntimeLibrary.PrepareOpti(cache, "gameA", "source/version", dll, null);
            Check(HysxFileLink.AreHardLinked(a, nrA)
                && File.ReadAllText(Path.Combine(Path.GetDirectoryName(gameA)!, "OptiScaler.ini")) == "game-A-config",
                "return to build default preserves game quality settings");
            Check(File.ReadAllText(existingDiagnostic) == "existing-evidence",
                "runtime synchronization does not delete existing diagnostic evidence");
            string pack = Path.Combine(root, "pack");
            var entries = new[] { new AddonPackEntry { FileName = NrRuntimeLibrary.FileName, SourcePath = a } };
            GameAddonPack.Sync(pack, entries, identity: "same");
            string packed = Path.Combine(pack, NrRuntimeLibrary.FileName);
            File.Delete(packed); File.Copy(a, packed);
            GameAddonPack.Sync(pack, entries, identity: "same");
            Check(HysxFileLink.AreHardLinked(a, packed), "unchanged pack identity repairs old NR copies");
            string target = Path.Combine(root, "locked", NrRuntimeLibrary.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "previous");
            using (var locked = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool rejected = false;
                try { HysxFileLink.LinkNeuralRuntime(a, target); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected && File.ReadAllText(target) == "previous", "locked target fails safely without in-place overwrite");
            }
            string missing = Path.Combine(root, "missing", NrRuntimeLibrary.FileName);
            bool failed = false;
            try { HysxFileLink.LinkNeuralRuntime(missing, target); } catch (IOException) { failed = true; }
            Check(failed && File.ReadAllText(target) == "previous", "missing source keeps old target");
            foreach (string zipPath in args.Skip(1))
            {
                string extract = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(extract);
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.Entries.Single(e => Path.GetFileName(e.FullName).Equals(NrRuntimeLibrary.FileName, StringComparison.OrdinalIgnoreCase));
                string path = Path.Combine(extract, NrRuntimeLibrary.FileName);
                entry.ExtractToFile(path);
                string hash = NrRuntimeLibrary.Hash(path);
                string? expected = NrRuntimeCatalog.Identify(entry.Length, hash);
                Check(expected is NrRuntimeCatalog.GenshinV2PresetA3000 or NrRuntimeCatalog.GenshinV23000, "user ZIP recognized: " + expected);
                var real = DllInstaller.Scan(extract).Single();
                Check(DllVariantResolver.Identify(null, family, real, "wrong", real.Size).Variant == expected, "real PE identified by content: " + expected);
                Console.WriteLine($"FINGERPRINT {expected}: size={entry.Length}, PE={real.Version}, SHA256={hash}");
                store.Archive("dlssnr", expected!, [path]);
                Check(HysxFileLink.AreHardLinked(path, store.FilePath("dlssnr", expected!, NrRuntimeLibrary.FileName)), "user runtime archive uses hardlink");
                Check(NrRuntimeCatalog.Identify(entry.Length, new string('0', 64)) is null, "same-size impostor rejected");
                string snapshot = JsonSerializer.Serialize(new DllFingerprintDocument { SchemaVersion = 1,
                    Fingerprints = DllFingerprintCatalog.Fingerprints.ToArray(), Packages = DllFingerprintCatalog.Packages.ToArray() });
                var changed = DllFingerprintCatalog.Parse(snapshot);
                int index = Array.FindIndex(changed.Fingerprints, f => f.Version == expected);
                changed.Fingerprints[index] = changed.Fingerprints[index] with { Version = "云端新增版本" };
                DllFingerprintCatalog.LoadJson(JsonSerializer.Serialize(changed));
                Check(DllVariantResolver.Identify(null, family, real, null, 0).Variant == "云端新增版本",
                    "cloud-only metadata update changes recognition without recompiling");
                changed.Fingerprints[index] = changed.Fingerprints[index] with { PeVersion = "999.0" };
                DllFingerprintCatalog.LoadJson(JsonSerializer.Serialize(changed));
                Check(!DllVariantResolver.Identify(null, family, real, null, 0).Known,
                    "matching hash with cloud PE-version mismatch rejected");
                changed.Fingerprints[index] = changed.Fingerprints[index] with { PeVersion = "310.9.16", Machine = "I386" };
                DllFingerprintCatalog.LoadJson(JsonSerializer.Serialize(changed));
                Check(!DllVariantResolver.Identify(null, family, real, null, 0).Known,
                    "matching hash with cloud PE-architecture mismatch rejected");
                bool invalid = false;
                try { DllFingerprintCatalog.LoadJson("{\"schemaVersion\":2}"); } catch (InvalidDataException) { invalid = true; }
                Check(invalid && NrRuntimeCatalog.Identify(entry.Length, hash) == "云端新增版本", "invalid cloud update keeps last valid snapshot");
                DllFingerprintCatalog.LoadJson(snapshot);
            }
            Console.WriteLine($"NR selection tests: PASS {passed}, FAIL 0");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
