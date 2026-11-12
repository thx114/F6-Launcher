using System.Diagnostics;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class LaunchPreparationTests
{
    // The installed source and canonical file must already be linked. This probe
    // exercises validation only; it does not deploy a runtime or start a game.
    public static int Probe(string cache, string version)
    {
        string source = new DllVersionStore(cache).FilePath("dlssnr", version, NrRuntimeLibrary.FileName);
        string hash = NrRuntimeLibrary.Hash(source);
        string canonical = Path.Combine(cache, "nr-content", hash, NrRuntimeLibrary.FileName);
        if (!HysxFileLink.AreHardLinked(source, canonical))
            throw new IOException("Probe requires an already interned, hard-linked runtime.");
        for (int i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            string result = NrRuntimeLibrary.VersionFile(cache, version);
            Console.WriteLine($"VersionFile round={i + 1} elapsedMs={watch.Elapsed.TotalMilliseconds:F1} bytes={new FileInfo(source).Length} linked={HysxFileLink.AreHardLinked(source, result)}");
        }
        return 0;
    }

    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
        }
        string root = Path.Combine(Path.GetTempPath(), "f6-launch-preparation-" + Guid.NewGuid().ToString("N"));
        try
        {
            string archive = Path.Combine(root, "dlls", "dlssnr", "fixture", NrRuntimeLibrary.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            File.WriteAllText(archive, "original-runtime");
            string canonical = NrRuntimeLibrary.VersionFile(root, "fixture");
            Check(HysxFileLink.AreHardLinked(archive, canonical), "First resolution interns by hard link");
            Check(NrRuntimeLibrary.VersionFile(root, "fixture") == canonical, "Repeated resolution returns the same content");
            DateTime originalWrite = File.GetLastWriteTimeUtc(archive);
            File.WriteAllText(archive, "modified-runtime");
            File.SetLastWriteTimeUtc(archive, originalWrite);
            string changed = NrRuntimeLibrary.VersionFile(root, "fixture");
            Check(changed != canonical, "Same-size edits with restored timestamps are hashed again across calls");
            Check(HysxFileLink.AreHardLinked(archive, changed), "Changed content is still deployed by hard link");

            // Break only the canonical link and poison that independent file.
            // The archive remains valid; canonical validation must fail closed.
            string staged = changed + ".replacement";
            File.WriteAllText(staged, "poisoned-runtime");
            File.Move(staged, changed, overwrite: true);
            bool rejected = false;
            try { NrRuntimeLibrary.VersionFile(root, "fixture"); }
            catch (IOException) { rejected = true; }
            Check(rejected, "A separate corrupted canonical file is rejected");
            Check(File.ReadAllText(archive) == "modified-runtime", "Failed validation preserves archive bytes");
            File.Delete(changed);
            Check(HysxFileLink.AreHardLinked(archive, NrRuntimeLibrary.VersionFile(root, "fixture")),
                "A missing canonical file is rebuilt as a hard link");

            var saved = new DllFingerprintDocument
            {
                SchemaVersion = 1, Fingerprints = DllFingerprintCatalog.Fingerprints.ToArray(),
                Packages = DllFingerprintCatalog.Packages.ToArray()
            };
            try
            {
                DllFingerprintCatalog.LoadJson(System.Text.Json.JsonSerializer.Serialize(new DllFingerprintDocument
                {
                    SchemaVersion = 1,
                    Fingerprints = [new DllFingerprint("dlssnr", NrRuntimeLibrary.FileName, "fixture",
                        new FileInfo(archive).Length, new string('0', 64), "1.0.0.0", "Amd64")]
                }));
                rejected = false;
                try { NrRuntimeLibrary.VersionFile(root, "fixture"); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Known versions still reject a fingerprint mismatch");

                string pe = new DllVersionStore(root).DirectoryFor("dlssnr", "fixture-pe");
                Directory.CreateDirectory(pe);
                pe = Path.Combine(pe, NrRuntimeLibrary.FileName);
                File.Copy(typeof(NrRuntimeLibrary).Assembly.Location, pe);
                using var input = File.OpenRead(pe);
                using var reader = new System.Reflection.PortableExecutable.PEReader(input);
                DllFingerprintCatalog.LoadJson(System.Text.Json.JsonSerializer.Serialize(new DllFingerprintDocument
                {
                    SchemaVersion = 1,
                    Fingerprints = [new DllFingerprint("dlssnr", NrRuntimeLibrary.FileName, "fixture-pe",
                        new FileInfo(pe).Length, NrRuntimeLibrary.Hash(pe), FileVersionInfo.GetVersionInfo(pe).FileVersion!,
                        reader.PEHeaders.CoffHeader.Machine.ToString())]
                }));
                input.Dispose();
                Check(HysxFileLink.AreHardLinked(pe, NrRuntimeLibrary.VersionFile(root, "fixture-pe")),
                    "Known matching SHA256 and PE are accepted and hard-linked");
            }
            finally { DllFingerprintCatalog.LoadJson(System.Text.Json.JsonSerializer.Serialize(saved)); }

            var host = new ShadeHost(Path.Combine(root, "host"));
            Directory.CreateDirectory(host.AddonsPath);
            File.WriteAllText(host.ReShadeIniPath, "[GENERAL]\nPresetPath=User.ini\n[RenoDX.DLSS5]\nNRResolutionScale=0.72\nNRPasses=2\n[ADDON]\nDisabledAddons=UserDisabled.addon64\n");
            string game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            var entry = new GameEntry("biz:hk4e_cn", "fixture") { ExePath = Path.Combine(game, "YuanShen.exe") };
            string addons = GameAddonPack.AddonDirectory(root, "hk4e_cn");
            int syncs = 0;
            void Sync()
            {
                Check(File.Exists(entry.ReShadeIniPath), "The target exists before package binding");
                syncs++;
                GameAddonPack.Sync(addons, [], identity: "fixture");
                GamePackIniBinding.Bind(entry, host, addons);
            }
            for (int launch = 0; launch < 2; launch++)
            {
                var result = GameLaunchIniPreparation.PrepareForLaunch(entry, host, Sync, () => (false, false));
                Check(!result.Failed, "Fresh and repeat preparations both complete");
                var primary = ReShadeProfile.Load(entry.ReShadeIniPath!);
                var secondary = ReShadeProfile.Load(Path.Combine(game, GameIniBootstrap.SecondaryIniFileName));
                Check(primary.AddonPath == addons && string.IsNullOrEmpty(secondary.AddonPath),
                    "One pass binds the primary package and preserves the plugin-free secondary runtime");
                Check(primary.GetValue("RenoDX.DLSS5", "NRResolutionScale") == "0.72"
                    && primary.GetValue("RenoDX.DLSS5", "NRPasses") == "2"
                    && primary.GetValue("ADDON", "DisabledAddons") == "UserDisabled.addon64",
                    "Preparation preserves user NR and disabled-plugin choices");
            }
            Check(syncs == 2, "Each launch synchronizes its package exactly once");
            Console.WriteLine($"PASS {checks} launch preparation checks; no game started.");
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
