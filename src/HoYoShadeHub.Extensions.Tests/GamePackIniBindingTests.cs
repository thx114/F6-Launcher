using System.Text.Json;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class GamePackIniBindingTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "f6-pack-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            passed++;
        }
        GameEntry Entry(string game)
        {
            string directory = Path.Combine(root, game);
            Directory.CreateDirectory(directory);
            return new GameEntry("biz:" + game, game) { ExePath = Path.Combine(directory, "fixture.exe") };
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (IOException) { rejected = true; }
            Check(rejected, message);
        }
        try
        {
            var host = new ShadeHost(Path.Combine(root, "host"));
            Directory.CreateDirectory(host.AddonsPath);
            File.WriteAllText(Path.Combine(host.RootPath, "ReShade64.dll"), "fixture, never loaded");
            File.WriteAllText(Path.Combine(host.AddonsPath, "fixture.addon64"), "fixture, never loaded");
            File.WriteAllText(host.ReShadeIniPath, "; shared template\n[ADDON]\nAddonPath=reshade-shaders\\Addons\n[GENERAL]\nPresetPath=User.ini\n");
            byte[] template = File.ReadAllBytes(host.ReShadeIniPath);
            string cache = Path.Combine(root, "cache");
            string pending = Path.Combine(cache, "games", ".pending-gamepacks", "hk4e");
            Directory.CreateDirectory(pending);
            File.WriteAllText(Path.Combine(pending, "ini_config.json"), "{}");

            var first = Entry("hk4e_cn");
            Check(PackPendingMigration.PlaceMatching(cache, "hk4e_cn") == 1, "fresh import migrates pending pack before binding");
            string packRoot = GameAddonPack.PackDirectory(cache, "hk4e_cn");
            string addons = GameAddonPack.AddonDirectory(cache, "hk4e_cn");
            var plan = GameAddonPack.Plan(host.AddonsPath, new Dictionary<string, string>(), (_, _) => []);
            GameAddonPack.Sync(addons, plan, []);
            Check(!File.Exists(Path.Combine(packRoot, "auto.json")), "fixture has no actions or consent to trigger binding");
            Check(GamePackIniBinding.Bind(first, host, addons) == Path.GetFullPath(addons), "import binds without auto.json");
            Check(File.Exists(first.ReShadeIniPath), "missing game INI is prepared without starting a game");
            Check(File.Exists(Path.Combine(ReShadeProfile.Load(first.ReShadeIniPath!).ResolveAddonDirectory()!, "fixture.addon64")), "bound INI resolves the actual installed plugin");
            Check(GameAddonPack.IsPackDirectory(addons), "bound target has managed pack marker so path alignment preserves it");
            Check(File.ReadAllBytes(host.ReShadeIniPath).SequenceEqual(template), "binding leaves shared template unchanged");

            File.WriteAllText(first.ReShadeIniPath!, "; user comment\n[ADDON]\nAddonPath=reshade-shaders\\Addons\nDisabledAddons=UserDisabled.addon64\n[GENERAL]\nPresetPath=User.ini\n[RenoDX.DLSS5]\nNRResolutionScale=0.5\nNRPasses=2\n");
            GamePackIniBinding.Bind(first, null, addons);
            var profile = ReShadeProfile.Load(first.ReShadeIniPath!);
            Check(profile.AddonPath == addons, "existing relative path is replaced with current pack's absolute path");
            Check(profile.GetValue("ADDON", "DisabledAddons") == "UserDisabled.addon64", "deferred/declined actions do not alter addon choices");
            Check(profile.PresetPath == "User.ini" && File.ReadAllText(first.ReShadeIniPath!).Contains("; user comment"), "preset and comments survive binding");
            Check(profile.GetValue("RenoDX.DLSS5", "NRResolutionScale") == "0.5" && profile.GetValue("RenoDX.DLSS5", "NRPasses") == "2", "NR user parameters survive binding");
            DateTime sentinelTime = new(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(first.ReShadeIniPath!, sentinelTime);
            byte[] boundBytes = File.ReadAllBytes(first.ReShadeIniPath!);
            GamePackIniBinding.Bind(first, host, addons + Path.DirectorySeparatorChar);
            Check(File.ReadAllBytes(first.ReShadeIniPath!).SequenceEqual(boundBytes)
                && File.GetLastWriteTimeUtc(first.ReShadeIniPath!) == sentinelTime, "repeat import binding does not rewrite unchanged INI");

            PackIniAction.Apply(new PackActionStep
            {
                Action = "set_ini_keys",
                Raw = JsonSerializer.SerializeToElement(new { set = new { ADDON = new { AddonPath = "reshade-shaders\\Addons" } } }),
            }, first, host);
            GamePackIniBinding.Bind(first, host, addons);
            Check(ReShadeProfile.Load(first.ReShadeIniPath!).AddonPath == addons, "post-action binding repairs a legacy package's relative-path write");
            using (var locked = new FileStream(first.ReShadeIniPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => GamePackIniBinding.Bind(first, host, addons), "locked INI fails instead of reporting bound");

            var second = Entry("hkrpg_cn");
            string secondAddons = GameAddonPack.AddonDirectory(cache, "hkrpg_cn");
            GameAddonPack.Sync(secondAddons, plan, []);
            File.Delete(host.ReShadeIniPath);
            GamePackIniBinding.Bind(second, host, secondAddons);
            Check(ReShadeProfile.Load(second.ReShadeIniPath!).AddonPath == secondAddons, "fresh host without template can bind another game");
            Check(ReShadeProfile.Load(first.ReShadeIniPath!).AddonPath == addons, "each game's INI retains its own addon path");
            Check(!File.Exists(host.ReShadeIniPath), "fallback does not generate a shared template");
            Check(!File.Exists(Path.Combine(second.GameDirectory!, "HoYoShade.UserNR.ini")), "binding does not invent an NR sidecar");

            var failed = Entry("failed");
            Reject(() => GamePackIniBinding.Bind(failed, host, Path.Combine(root, "absent-addons")), "missing plugin target rejects binding");
            Check(!File.Exists(failed.ReShadeIniPath), "invalid plugin target does not create a game INI");
            Reject(() => GamePackIniBinding.Bind(failed, null, addons), "missing INI without available host fails");
            Check(!File.Exists(failed.ReShadeIniPath), "failed target preparation does not leave an empty INI");
            Check(!Directory.EnumerateFiles(root, ".f6-*.tmp", SearchOption.AllDirectories).Any(), "no creation staging files remain");
            Console.WriteLine($"Game pack INI binding: PASS {passed} / FAIL 0; GameTested=false.");
            return passed;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
