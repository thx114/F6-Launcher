using System.Security.Cryptography;
using System.Text.Json;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PackPermissionTests
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "f6-pack-permissions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { Check(true, message); return; }
            throw new InvalidOperationException(message);
        }
        try
        {
            var time = new ManualTime();
            var delay = new ConfirmationDelay(time);
            Check(!delay.CanApply && delay.RemainingSeconds == 3, "Not enabled before opening");
            time.Advance(9000);
            Check(!delay.CanApply, "Pre-open time does not count");
            delay.Start();
            time.Advance(1001);
            Check(!delay.CanApply && delay.RemainingSeconds == 2, "Countdown uses elapsed time");
            time.Advance(1998);
            Check(!delay.CanApply && delay.RemainingSeconds == 1, "2999 ms cannot apply");
            time.Advance(1);
            Check(delay.CanApply && delay.RemainingSeconds == 0, "3000 ms enables apply");
            delay.Start();
            Check(!delay.CanApply && delay.RemainingSeconds == 3, "Reopen restarts countdown");

            string pack = Path.Combine(root, "pack");
            Directory.CreateDirectory(pack);
            File.WriteAllText(Path.Combine(pack, "auto.json"), """
                {"actions":[{"name":"配置|A","runOnLaunch":true,"steps":[
                  {"action":"set_ini_keys","id":"declined-first"},
                  {"action":"copy_file","id":"selected"},
                  {"action":"set_ini_keys","id":"declined-last"}]}]}
                """);
            var snapshot = PackAutoActionSnapshot.TryLoad(pack)!;
            var action = snapshot.LoadActions().Single();
            var selection = new PackStepSelection(new(StringComparer.Ordinal),
                new([PackStepSelection.Key(action, 0), PackStepSelection.Key(action, 2)], StringComparer.Ordinal));
            var old = PackConsentRecord.Parse(PackConsentRecord.Serialize(snapshot.Hash, []))!;
            Check(old.DisabledSteps.Count == 0 && old.Selection.IsEnabled(action, 0), "Schema 1 remains compatible");
            var saved = PackConsentRecord.Parse(PackConsentRecord.Serialize(snapshot.Hash, [], selection.DisabledSteps))!;
            Check(saved.Selection.HasEnabledSteps(action) && !saved.Selection.IsEnabled(action, 0)
                && saved.Selection.IsEnabled(action, 1) && !saved.Selection.IsEnabled(action, 2), "Schema 2 roundtrips each step");
            foreach (string raw in new[]
            {
                JsonSerializer.Serialize(new { schema = 2, hash = snapshot.Hash, disabled = Array.Empty<string>() }),
                JsonSerializer.Serialize(new { schema = 2, hash = snapshot.Hash, disabled = Array.Empty<string>(), disabledSteps = new[] { "x", "x" } }),
                JsonSerializer.Serialize(new { schema = 2, hash = snapshot.Hash, disabled = Array.Empty<string>(), disabledSteps = new[] { " " } }),
                JsonSerializer.Serialize(new { schema = 1, hash = snapshot.Hash, disabled = Array.Empty<string>(), disabledSteps = Array.Empty<string>() }),
            }) Check(PackConsentRecord.Parse(raw) is null, "Malformed consent rejected");
            var groupDisabled = new PackStepSelection(new([action.Name], StringComparer.Ordinal), new(StringComparer.Ordinal));
            Check(!groupDisabled.HasEnabledSteps(action), "Legacy group decline disables every step");
            // Only the declined steps require ReShade. Their prerequisites must never run.
            GameLaunchIniPreparation.EnsurePackActionTarget(null, null, [action], selection.IsEnabled);
            Check(true, "Unchecked INI steps do not initialize or require a host");
            var calls = new List<string>();
            Task<string> Dispatch(PackActionStep step) { calls.Add(step.GetString("id")!); return Task.FromResult("完成"); }
            var first = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true,
                Dispatch, isStepEnabled: selection.IsEnabled);
            Check(!first.Blocked && calls.SequenceEqual(["selected"]), "Only checked original step dispatches");
            using (var ledger = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, PackOnceLedger.FileName))))
            {
                var states = ledger.RootElement.GetProperty("states");
                Check(states.EnumerateObject().Count() == 1
                    && states.GetProperty(PackActionOncePolicy.StableKey(action.Name, 1, action.Steps[1])).GetString() == "success",
                    "Declined steps have neither pending nor success states");
                Check(ledger.RootElement.GetProperty("plan").GetArrayLength() == 3, "Plan preserves all original identities");
            }
            var repeated = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true,
                Dispatch, isStepEnabled: selection.IsEnabled);
            Check(!repeated.Blocked && calls.Count == 1, "Startup preserves declines and once completions");
            var all = new PackStepSelection(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            var approved = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true,
                Dispatch, isStepEnabled: all.IsEnabled);
            Check(!approved.Blocked && calls.SequenceEqual(["selected", "declined-first", "declined-last"]),
                "Later explicit approval runs only previously declined steps with original identities");
            var contextChanged = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => false, Dispatch,
                isStepEnabled: all.IsEnabled);
            Check(contextChanged.Blocked && calls.Count == 3, "Context drift dispatches nothing");

            string game = Path.Combine(root, "game"), launcher = Path.Combine(root, "launcher"), shade = Path.Combine(root, "shade");
            Directory.CreateDirectory(game);
            string source = Path.Combine(pack, "payload.ini");
            File.WriteAllText(source, "new content");
            string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            PackActionStep Step(string directory = "Config", string target = "game", string file = "payload.ini",
                string? name = null, bool overwrite = true, string? sha256 = null)
            {
                var raw = JsonSerializer.SerializeToElement(new { action = "copy_file", file, directory, target, name, overwrite, sha256 });
                return new PackActionStep { Action = "copy_file", Raw = raw };
            }
            PackFilePlacement.Plan Resolve(PackActionStep step) => PackFilePlacement.Resolve(step, pack, game, launcher, shade);
            var plan = Resolve(Step(name: "renamed.ini", sha256: digest));
            var placed = PackFilePlacement.Apply(plan, pack);
            Check(placed.Changed && placed.Backup is null && File.ReadAllText(plan.Destination) == "new content",
                "Single file creates target directories and supports rename/hash");
            File.WriteAllText(source, "later source edit");
            Check(File.ReadAllText(plan.Destination) == "new content" && !HysxFileLink.AreHardLinked(source, plan.Destination),
                "Normal file placement does not link mutable config back to package");
            File.WriteAllText(source, "new content");
            Check(!PackFilePlacement.Apply(plan, pack).Changed, "Identical content is not rewritten");
            foreach (var (target, expectedRoot) in new[] { ("game", game), ("launcher", launcher), ("shade", shade) })
                Check(Resolve(Step(".", target)).Destination == Path.Combine(expectedRoot, "payload.ini"), "Relative root " + target);
            Check(Resolve(Step(Path.Combine(root, "absolute"))).Destination == Path.Combine(root, "absolute", "payload.ini"),
                "Explicit absolute destination accepted");
            File.WriteAllText(plan.Destination, "old content");
            string sibling = Path.Combine(game, "linked-old.ini");
            HysxFileLink.LinkOrCopy(plan.Destination, sibling);
            Check(HysxFileLink.AreHardLinked(plan.Destination, sibling), "Fixture has actual hardlink");
            var replaced = PackFilePlacement.Apply(plan, pack);
            Check(replaced.Backup is not null && File.ReadAllText(replaced.Backup) == "old content"
                && File.Exists(replaced.Backup + ".json"), "Overwrite records independent original backup and destination");
            Check(File.ReadAllText(sibling) == "old content" && File.ReadAllText(plan.Destination) == "new content",
                "Replacement leaves old hardlink siblings unchanged");
            File.WriteAllText(source, "different");
            Reject(() => PackFilePlacement.Apply(plan, pack), "Incorrect source hash blocks overwrite");
            Reject(() => PackFilePlacement.Apply(Resolve(Step(name: "renamed.ini", overwrite: false)), pack), "No overwrite refuses different target");
            Check(File.ReadAllText(plan.Destination) == "new content", "Rejected operations preserve original destination");
            Reject(() => Resolve(Step(file: "../escape.ini")), "Source traversal rejected");
            Reject(() => Resolve(Step(file: source)), "Absolute source rejected");
            Reject(() => Resolve(Step(directory: "../outside")), "Relative destination traversal rejected");
            Reject(() => Resolve(Step(name: "../bad.ini")), "Output name cannot be a directory");
            Reject(() => Resolve(Step(file: "missing.ini")), "Missing payload rejected");
            var lockedPlan = Resolve(Step(name: "renamed.ini"));
            using (var held = new FileStream(lockedPlan.Destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => PackFilePlacement.Apply(lockedPlan, pack), "Locked destination fails without truncation");
            Check(!Directory.EnumerateFiles(game, ".f6-place-*", SearchOption.AllDirectories).Any(), "Failed placement cleans staged files");
            string nr = Path.Combine(pack, "nvngx_dlssnr.dll");
            File.WriteAllText(nr, "NR transport fixture; never loaded");
            var nrPlan = Resolve(Step(file: "nvngx_dlssnr.dll"));
            PackFilePlacement.Apply(nrPlan, pack);
            Check(HysxFileLink.AreHardLinked(nr, nrPlan.Destination), "NR uses verified strict hardlink");
            string independent = Path.Combine(game, "independent", "nvngx_dlssnr.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(independent)!);
            File.Copy(nr, independent);
            var relink = Resolve(Step(directory: Path.GetDirectoryName(independent)! , file: "nvngx_dlssnr.dll"));
            Check(PackFilePlacement.Apply(relink, pack).Changed && HysxFileLink.AreHardLinked(nr, independent),
                "Equal content NR copy is replaced with actual hardlink");
            Console.WriteLine($"PASS {passed} pack permission, countdown and file placement checks");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(long milliseconds) => _timestamp += milliseconds;
    }
}
