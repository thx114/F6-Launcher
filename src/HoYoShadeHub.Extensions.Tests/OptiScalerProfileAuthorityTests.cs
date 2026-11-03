using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.OptiScaler;

namespace HoYoShadeHub.Extensions.Tests;

internal static class OptiScalerProfileAuthorityTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-profile-authority-" + Guid.NewGuid().ToString("N"));
        int passed = 0;
        void Check(bool ok, string name)
        { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
        const string game = "hk4e_cn";
        const string initial = "[FrameGen]\nEnabled=false\n[NR]\nQuality=2\n[Libraries]\nOptiDllPath=auto\n";
        string source = Path.Combine(root, "official", "DLSS F5"), runtime = Path.Combine(root, "runtime");
        string main = Path.Combine(runtime, "OptiScaler.ini");
        string official = OptiScalerProfiles.ProfilePath(source, game);
        string cacheProfile = OptiScalerProfiles.ProfilePath(runtime, game);
        string cacheBaseline = Path.Combine(runtime, OptiScalerProfiles.RuntimeBaselineFileName);
        string authorityBaseline = Path.Combine(runtime, ".hub-authority-baseline");
        // GetValue preserves whitespace after '='; compare semantic values, not INI formatting.
        string Value(string path, string section, string key) =>
            IniDocument.LoadFromText(File.ReadAllText(path)).GetValue(section, key)?.Trim() ?? "<missing>";
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(source, "OptiScaler.ini"), initial);
            Check(OptiScalerProfiles.Activate(source, game), "official fixture activated");
            File.WriteAllText(official, initial.Replace("Enabled=false", "Enabled=true"));
            File.WriteAllText(main, initial.Replace("Enabled=false", "Enabled=auto"));
            Check(OptiScalerProfiles.PrepareAuthoritativeProfile(runtime, source, game), "first official projection succeeds");
            Check(Value(main, "FrameGen", "Enabled") == "true", "official true replaces stale untracked cache auto");
            Check(Directory.GetDirectories(Path.Combine(runtime, "profile-recovery")).Length == 1,
                "initial cache is archived");
            Check(OptiScalerProfiles.AuthoritativeBuildDirectory(runtime) == source, "runtime resolves official build identity");
            // Simultaneous profile edit and runtime Save Settings: official wins same key,
            // while unrelated game changes are collected.
            File.WriteAllText(official, File.ReadAllText(official).Replace("Enabled=true", "Enabled=false"));
            File.WriteAllText(main, File.ReadAllText(main).Replace("Enabled=true", "Enabled=auto")
                .Replace("Quality=2", "Quality=4").Replace("OptiDllPath=auto", @"OptiDllPath=D:\runtime\OptiScaler"));
            Check(OptiScalerProfiles.Store(runtime, game), "exit collection succeeds");
            Check(Value(official, "FrameGen", "Enabled") == "false", "new official FG edit wins stale runtime collision");
            Check(Value(official, "NR", "Quality") == "4", "independent runtime quality saved to official");
            Check(Value(official, "Libraries", "OptiDllPath") == "auto", "runtime machine path not persisted to official");
            Check(Value(main, "Libraries", "OptiDllPath") == @"D:\runtime\OptiScaler", "runtime machine path retained in cache");
            Check(Value(cacheProfile, "Libraries", "OptiDllPath") == @"D:\runtime\OptiScaler",
                "runtime machine path retained in cache profile");
            Check(Value(cacheBaseline, "Libraries", "OptiDllPath") == @"D:\runtime\OptiScaler",
                "runtime machine path retained in cache baseline");
            Check(Value(authorityBaseline, "Libraries", "OptiDllPath") == "auto",
                "runtime machine path not persisted to authority baseline");
            Check(Value(main, "FrameGen", "Enabled") == "false" && Value(main, "NR", "Quality") == "4",
                "cache projection keeps official collision winner and independent runtime edit");
            Check(File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(cacheProfile)),
                "cache profile synchronized");
            Check(File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(cacheBaseline)),
                "cache baseline synchronized with runtime projection");
            Check(File.ReadAllBytes(official).SequenceEqual(File.ReadAllBytes(authorityBaseline)),
                "authority baseline synchronized with official instead of machine-path cache");
            Check(OptiScalerProfiles.PrepareAuthoritativeProfile(runtime, source, game)
                && Value(main, "FrameGen", "Enabled") == "false", "repeat launch projects latest official");
            File.WriteAllText(official, File.ReadAllText(official).Replace("Enabled=false", "Enabled=true"));
            Check(OptiScalerProfiles.PrepareAuthoritativeProfile(runtime, source, game)
                && Value(main, "FrameGen", "Enabled") == "true", "official edits after cache creation are reprojected");
            byte[] beforeLocked = File.ReadAllBytes(official);
            File.WriteAllText(main, File.ReadAllText(main).Replace("Quality=4", "Quality=5"));
            byte[] changedRuntime = File.ReadAllBytes(main);
            using (File.Open(official, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(!OptiScalerProfiles.Store(runtime, game), "locked authority rejects commit");
            Check(File.ReadAllBytes(official).SequenceEqual(beforeLocked)
                && File.ReadAllBytes(main).SequenceEqual(changedRuntime), "failed commit preserves official and runtime edits");
            Check(OptiScalerProfiles.Store(runtime, game)
                && Value(official, "NR", "Quality") == "5", "retry after lock release collects original runtime edit");
            string another = OptiScalerProfiles.ProfilePath(source, "hkrpg_cn");
            File.WriteAllText(another, initial);
            Check(!OptiScalerProfiles.PrepareAuthoritativeProfile(runtime, source, "hkrpg_cn")
                && File.ReadAllText(another) == initial, "different game cannot reuse binding");
            byte[] current = File.ReadAllBytes(main);
            File.Delete(official);
            Check(!OptiScalerProfiles.Store(runtime, game)
                && !OptiScalerProfiles.RecoverForLaunch(runtime).Succeeded
                && File.ReadAllBytes(main).SequenceEqual(current) && !File.Exists(official),
                "missing authority blocks without recreating from stale runtime");
            TestNoCacheLaunch(root, Check);
            TestInterleavings(root, Check);
            Console.WriteLine($"Profile authority: PASS {passed}, FAIL 0; GameTested=false.");
            return 0;
        }
        finally
        {
            OptiScalerProfiles.AuthorityTestCheckpoint.Value = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void TestNoCacheLaunch(string root, Action<bool, string> check)
    {
        string oldRoot = AppConfig.OptiScalerRootPath;
        var oldValues = new Dictionary<string, string>(AppConfig.Values);
        const string game = "hk4e_cn", preset = "[FrameGen]\nEnabled=false\n";
        string library = Path.Combine(root, "no-cache-library"), source = Path.Combine(library, "official", "DLSS F5");
        try
        {
            AppConfig.OptiScalerRootPath = library;
            AppConfig.Values.Clear();
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "OptiScaler.ini"), preset);
            check(OptiScalerProfiles.Activate(source, game), "no-cache official fixture activation");
            Directory.CreateDirectory(OptiScalerPresets.Root);
            File.WriteAllText(OptiScalerPresets.PathOf("stale"), preset);
            AppConfig.Values[OptiScalerPresets.PresetKey(game, "official/DLSS F5")] = "stale";
            string profile = OptiScalerProfiles.ProfilePath(source, game);
            string edit = preset.Replace("false", "true");
            File.WriteAllText(profile, edit);
            var prepared = OptiScalerPresets.SyncIntoProfileForLaunchChecked(source, game);
            check(prepared.Succeeded && File.ReadAllText(profile) == edit
                && OptiScalerProfiles.Activate(source, game)
                && File.ReadAllText(Path.Combine(source, "OptiScaler.ini")) == edit,
                "direct official launch with no NR/cache preserves newer official over stale named preset");
            check(File.ReadAllText(OptiScalerPresets.PathOf("stale")) == preset,
                "implicit launch does not alter stale preset when runtime unchanged");
            check(OptiScalerPresets.Apply(source, game, "stale")
                && File.ReadAllText(profile) == preset,
                "explicit preset Apply still replaces official profile");
        }
        finally
        {
            AppConfig.OptiScalerRootPath = oldRoot;
            AppConfig.Values.Clear();
            foreach (var pair in oldValues) AppConfig.Values.Add(pair.Key, pair.Value);
        }
    }

    private static void TestInterleavings(string root, Action<bool, string> check)
    {
        const string game = "hk4e_cn", initial = "[FrameGen]\nEnabled=false\n[NR]\nQuality=2\n";
        (string Source, string Runtime, string Official, string Main) Fixture(string name)
        {
            string source = Path.Combine(root, name, "official"), runtime = Path.Combine(root, name, "runtime");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(source, "OptiScaler.ini"), initial);
            if (!OptiScalerProfiles.Activate(source, game)) throw new Exception("Fault fixture initialization");
            File.WriteAllText(Path.Combine(runtime, "OptiScaler.ini"), initial);
            return (source, runtime, OptiScalerProfiles.ProfilePath(source, game), Path.Combine(runtime, "OptiScaler.ini"));
        }
        var beforeSnapshot = Fixture("edit-before-snapshot");
        string newer = initial.Replace("Enabled=false", "Enabled=true");
        try
        {
            OptiScalerProfiles.AuthorityTestCheckpoint.Value = phase =>
            {
                if (phase == "before-projection-snapshot") File.WriteAllText(beforeSnapshot.Official, newer);
            };
            check(OptiScalerProfiles.PrepareAuthoritativeProfile(beforeSnapshot.Runtime, beforeSnapshot.Source, game)
                && File.ReadAllText(beforeSnapshot.Main) == newer
                && File.ReadAllText(Path.Combine(beforeSnapshot.Runtime, ".hub-authority-baseline")) == newer,
                "projection and authority baseline use the same newer snapshot observation");
        }
        finally { OptiScalerProfiles.AuthorityTestCheckpoint.Value = null; }

        var untouched = Fixture("rollback-untouched-official");
        try
        {
            OptiScalerProfiles.AuthorityTestCheckpoint.Value = phase =>
            {
                if (phase == "after-write:" + OptiScalerProfiles.ProfilePath(untouched.Runtime, game))
                {
                    File.WriteAllText(untouched.Official, newer);
                    throw new IOException("Injected runtime write failure with independent official edit.");
                }
            };
            check(!OptiScalerProfiles.PrepareAuthoritativeProfile(untouched.Runtime, untouched.Source, game)
                && File.ReadAllText(untouched.Official) == newer && File.ReadAllText(untouched.Main) == initial,
                "failed projection never restores untouched official over external edit");
            check(File.Exists(Path.Combine(untouched.Runtime, OptiScalerProfiles.RuntimeTransactionFileName)),
                "external edit during failed projection keeps blocking marker");
        }
        finally { OptiScalerProfiles.AuthorityTestCheckpoint.Value = null; }

        var ownWrite = Fixture("rollback-own-write");
        try
        {
            File.WriteAllText(ownWrite.Official, newer);
            OptiScalerProfiles.AuthorityTestCheckpoint.Value = phase =>
            {
                if (phase == "after-write:" + ownWrite.Main) throw new IOException("Injected failure after runtime replace.");
            };
            check(!OptiScalerProfiles.PrepareAuthoritativeProfile(ownWrite.Runtime, ownWrite.Source, game)
                && File.ReadAllText(ownWrite.Main) == initial && File.ReadAllText(ownWrite.Official) == newer
                && !File.Exists(Path.Combine(ownWrite.Runtime, OptiScalerProfiles.RuntimeTransactionFileName)),
                "failed projection restores only its own runtime writes and clears marker after complete rollback");
        }
        finally { OptiScalerProfiles.AuthorityTestCheckpoint.Value = null; }

        var replaced = Fixture("rollback-replaced-official");
        check(OptiScalerProfiles.PrepareAuthoritativeProfile(replaced.Runtime, replaced.Source, game),
            "collection conflict fixture projected");
        File.WriteAllText(replaced.Main, initial.Replace("Quality=2", "Quality=4"));
        string external = initial.Replace("Quality=2", "Quality=7");
        try
        {
            OptiScalerProfiles.AuthorityTestCheckpoint.Value = phase =>
            {
                if (phase == "after-write:" + replaced.Official)
                {
                    File.WriteAllText(replaced.Official, external);
                    throw new IOException("Injected failure after external replacement of our official write.");
                }
            };
            check(!OptiScalerProfiles.Store(replaced.Runtime, game)
                && File.ReadAllText(replaced.Official) == external
                && File.Exists(Path.Combine(replaced.Runtime, OptiScalerProfiles.RuntimeTransactionFileName)),
                "collection rollback preserves newer external replacement of transaction-owned official and blocks");
        }
        finally { OptiScalerProfiles.AuthorityTestCheckpoint.Value = null; }
    }
}
