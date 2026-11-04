using System.Text.Json;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PackIniActionTests
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "f6-pack-ini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            passed++;
        }
        GameEntry Entry(string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            return new GameEntry("biz:hk4e_cn", name) { ExePath = Path.Combine(dir, "YuanShen.exe") };
        }
        PackActionStep Step(string action = "set_ini_keys", string? file = null) => new()
        {
            Action = action,
            Raw = JsonSerializer.SerializeToElement(new
            {
                action, file,
                set = new Dictionary<string, object>
                {
                    ["GENERAL"] = new { PresetPath = "Package.ini" },
                    ["INPUT"] = new { KeyOverlay = "36,,0,,0,,0" },
                },
                remove = new Dictionary<string, string[]> { ["GENERAL"] = ["OldKey"] },
            }),
        };
        try
        {
            var host = new ShadeHost(Path.Combine(root, "host-without-template"));
            Directory.CreateDirectory(host.RootPath);
            File.WriteAllText(Path.Combine(host.RootPath, "ReShade64.dll"), "fixture, never loaded");
            foreach (string alias in new[] { "set_ini_keys", "set_ini", " WRITE_INI " })
            {
                var entry = Entry("first-install-" + passed);
                PackIniAction.Result result = PackIniAction.Apply(Step(alias), entry, host);
                var ini = IniDocument.Load(entry.ReShadeIniPath!);
                Check(result.Created && result.Changed, alias + " creates missing main INI without launching anything");
                Check(ini.GetValue("ADDON", "AddonPath") == host.AddonsPath, "generated INI points to the current host addons");
                Check(ini.GetValue("GENERAL", "EffectSearchPaths") == Path.Combine(host.RootPath, "reshade-shaders", "Shaders", "**"), "generated shader paths are absolute");
                Check(ini.GetValue("GENERAL", "PresetPath") == "Package.ini", "package action applies after generation");
                Check(ini.GetValue("INPUT", "KeyOverlay") == "36,0,0,0", "existing hotkey normalization remains active");
                Check(!File.Exists(host.ReShadeIniPath), "fallback never changes or creates shared host template");
                Check(!File.Exists(Path.Combine(entry.GameDirectory!, "HoYoShade.UserNR.ini")), "main INI initialization does not require an NR sidecar");
            }

            File.WriteAllText(host.ReShadeIniPath, "; original template\r\n[GENERAL]\r\nFromTemplate=yes\r\n");
            byte[] template = File.ReadAllBytes(host.ReShadeIniPath);
            var explicitMain = Entry("explicit-main");
            var mainAction = new PackAutoAction();
            mainAction.Steps.Add(Step(" set_ini ", "ReShade.ini"));
            GameLaunchIniPreparation.EnsurePackActionTarget(explicitMain, host, [mainAction]);
            Check(File.ReadAllBytes(explicitMain.ReShadeIniPath!).SequenceEqual(template), "explicit ReShade.ini alias uses exact template bytes");
            Check(File.ReadAllBytes(host.ReShadeIniPath).SequenceEqual(template), "template is preserved");

            var existing = Entry("existing");
            File.WriteAllText(existing.ReShadeIniPath!, "; user comment\n[GENERAL]\nOldKey=remove\nPresetPath=Old.ini\n[USER]\nKeep=yes\n[RenoDX.DLSS5]\nNRResolutionScale=0.5\nNRPasses=2\n");
            PackIniAction.Apply(Step(), existing, null);
            var edited = IniDocument.Load(existing.ReShadeIniPath!);
            Check(edited.GetValue("USER", "Keep") == "yes" && File.ReadAllText(existing.ReShadeIniPath!).Contains("; user comment"), "unrelated settings and comments survive");
            Check(edited.GetValue("RenoDX.DLSS5", "NRResolutionScale") == "0.5" && edited.GetValue("RenoDX.DLSS5", "NRPasses") == "2", "unrequested NR settings remain unchanged");
            Check(!edited.ContainsKey("GENERAL", "OldKey"), "declared removals apply");
            byte[] after = File.ReadAllBytes(existing.ReShadeIniPath!);
            Check(!PackIniAction.Apply(Step(), existing, null).Changed && File.ReadAllBytes(existing.ReShadeIniPath!).SequenceEqual(after), "identical values do not rewrite the file");

            var named = Entry("named");
            var custom = PackIniAction.Apply(Step(file: "PackageSettings.ini"), named, null);
            Check(custom.Created && IniDocument.Load(custom.Path).GetValue("GENERAL", "PresetPath") == "Package.ini", "explicit named INI is created with its requested values");
            Check(!File.Exists(named.ReShadeIniPath), "named-only action does not create a main INI");
            var removeOnly = new PackActionStep { Action = "set_ini_keys", Raw = JsonSerializer.SerializeToElement(new { file = "Absent.ini", remove = new { GENERAL = new[] { "OldKey" } } }) };
            Check(!PackIniAction.Apply(removeOnly, named, null).Created && !File.Exists(Path.Combine(named.GameDirectory!, "Absent.ini")), "removing from absent named INI does not create an empty file");
            foreach (string file in new[] { "../outside.ini", Path.Combine(root, "absolute.ini") })
            {
                bool rejected = false;
                try { PackIniAction.Apply(Step(file: file), named, null); }
                catch (IOException) { rejected = true; }
                Check(rejected, "named INI stays inside the declared game directory");
            }
            var empty = Entry("empty-action");
            bool emptyRejected = false;
            try { PackIniAction.Apply(new() { Action = "set_ini_keys", Raw = JsonSerializer.SerializeToElement(new { set = new { } }) }, empty, host); }
            catch (InvalidDataException) { emptyRejected = true; }
            Check(emptyRejected && !File.Exists(empty.ReShadeIniPath), "invalid action cannot leave an empty target behind");
            using (var locked = new FileStream(existing.ReShadeIniPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool failed = false;
                try { PackIniAction.Apply(Step(), existing, null); }
                catch (IOException) { failed = true; }
                Check(failed, "locked INI fails rather than reporting unchanged/success");
            }

            // Reproduce the reported chain: one completed addon step, then an old missing-INI failure.
            string packRoot = Path.Combine(root, "pending-pack");
            Directory.CreateDirectory(packRoot);
            File.WriteAllText(Path.Combine(packRoot, "auto.json"), """
                {"actions":[{"name":"legacy 4.2","steps":[
                  {"action":"set_addons","enabled":false},
                  {"action":"set_ini_keys","set":{"GENERAL":{"PresetPath":"Package.ini"}}},
                  {"action":"set_launch_option","key":"usehoyoshade","enabled":true}]}]}
                """);
            var snapshot = PackAutoActionSnapshot.TryLoad(packRoot)!;
            var action = snapshot.LoadActions()[0];
            var pendingEntry = Entry("pending-game");
            int addonCalls = 0, optionCalls = 0, prompts = 0;
            Task<string> OldDispatch(PackActionStep step)
            {
                if (step.Action == "set_addons") { addonCalls++; return Task.FromResult("已禁用插件"); }
                return Task.FromResult("✗ 找不到目标 ini");
            }
            var oldResult = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, packRoot, () => true, OldDispatch);
            Check(oldResult.Blocked && addonCalls == 1, "fixture preserves the first success and records the old failure as pending");
            byte[] pendingLedger = File.ReadAllBytes(Path.Combine(packRoot, PackOnceLedger.FileName));
            Task<string> FixedDispatch(PackActionStep step)
            {
                if (step.Action == "set_addons") { addonCalls++; return Task.FromResult("已禁用插件"); }
                if (step.Action == "set_launch_option") { optionCalls++; return Task.FromResult("已启用"); }
                PackIniAction.Apply(step, pendingEntry, host);
                return Task.FromResult("已写入 INI");
            }
            var automatic = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, packRoot, () => true, FixedDispatch);
            Check(automatic.Reason == PackActionOncePolicy.BlockReason.Pending && !File.Exists(pendingEntry.ReShadeIniPath), "background launch never replays a pending step");
            var declined = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, packRoot, () => true, FixedDispatch,
                confirmPendingIniRetry: _ => { prompts++; return Task.FromResult(false); });
            Check(declined.Blocked && prompts == 1 && File.ReadAllBytes(Path.Combine(packRoot, PackOnceLedger.FileName)).SequenceEqual(pendingLedger), "declining leaves pending and completed ledger bytes unchanged");
            bool current = true;
            var drifted = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, packRoot, () => current, FixedDispatch,
                confirmPendingIniRetry: _ => { current = false; return Task.FromResult(true); });
            Check(drifted.Reason == PackActionOncePolicy.BlockReason.ContextChanged && File.ReadAllBytes(Path.Combine(packRoot, PackOnceLedger.FileName)).SequenceEqual(pendingLedger), "context change during confirmation cannot release pending");
            var recovered = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, packRoot, () => true, FixedDispatch,
                confirmPendingIniRetry: _ => Task.FromResult(true));
            Check(!recovered.Blocked && addonCalls == 1 && optionCalls == 1, "explicit recovery resumes after completed steps and finishes the chain");
            Check(IniDocument.Load(pendingEntry.ReShadeIniPath!).GetValue("GENERAL", "PresetPath") == "Package.ini", "recovered missing target contains the requested values");
            byte[] recoveredIni = File.ReadAllBytes(pendingEntry.ReShadeIniPath!);
            var repeated = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, packRoot, () => true, FixedDispatch,
                confirmPendingIniRetry: _ => throw new Exception("Completed steps must not prompt"));
            Check(!repeated.Blocked && addonCalls == 1 && optionCalls == 1 && File.ReadAllBytes(pendingEntry.ReShadeIniPath!).SequenceEqual(recoveredIni), "successful once steps are never replayed by recovery");
            Check(!Directory.EnumerateFiles(root, ".f6-*.tmp", SearchOption.AllDirectories).Any(), "no creation staging files remain");
            Console.WriteLine($"Pack INI action: PASS {passed} / FAIL 0; GameTested=false.");
            return passed;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
