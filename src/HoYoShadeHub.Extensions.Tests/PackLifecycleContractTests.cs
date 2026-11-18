using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Features.Plugins;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.OptiScaler;

namespace HoYoShadeHub.Extensions.Tests;

/// <summary>
/// Offline contract verification, with real ZIP installation, INI files, once ledgers and profiles.
/// The UI import callback is represented explicitly by ImportAsync; it is not a UI acceptance test.
/// Actions whose production dispatcher lives in the WinUI host remain visible coverage gaps.
/// </summary>
internal static class PackLifecycleContractTests
{
    private static readonly string[] Games = ["hk4e_cn", "hkrpg_cn", "nap_cn"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record CheckResult(string Id, string Status, string Detail, long Milliseconds);
    private sealed record ActionContract(string Name, string[] Aliases, string? Service, string Gap);
    private sealed record ActionResult(string Action, string Canonical, bool Parsed, bool PolicyExecuted,
        bool OncePreserved, bool DisabledPreserved, bool RealServiceExecuted, string Evidence, string Gap);

    // Explicit reviewable inventory. The declaration test detects additions/removals in the real dispatcher.
    private static readonly ActionContract[] Contracts =
    [
        new("require_launcher_version", ["request_launcher_version"], nameof(PackLocalActionExecutor), ""),
        new("set_addons", ["set_addon"], nameof(GamePluginService), ""),
        new("import_preset", [], nameof(Dlss5PresetLibrary), ""),
        new("apply_preset", [], nameof(ReShadeProfile), ""),
        new("clear_game_ini", [], nameof(PackLocalActionExecutor), ""),
        new("set_ini_keys", ["set_ini", "write_ini"], nameof(PackIniAction), ""),
        new("set_dx12", [], null, "AppConfig game settings adapter is not linked."),
        new("set_smooth_motion", ["set_ai_frame", "set_ai_interpolation"], null, "GPU/driver and page integration is outside the offline fixture."),
        new("set_inject_mode", [], null, "AppConfig game settings adapter is not linked."),
        new("set_cmd_launch", [], null, "AppConfig game settings adapter is not linked."),
        new("set_cmd_args", [], null, "AppConfig argument adapter is not linked."),
        new("set_launch_option", [], null, "AppConfig game settings adapter is not linked."),
        new("reset_launch_options", [], null, "Production per-game reset adapter is not linked."),
        new("set_opt", ["set_optiscaler"], null, "AppConfig selection adapter is not linked."),
        new("set_opt_build", ["set_optiscaler_build", "select_opt_build"], null, "Production build selection/persistence adapter is not linked."),
        new("set_opt_nr", ["set_optiscaler_nr"], null, "Production hash/PE validation and selection adapter is not linked; a transport fixture is insufficient."),
        new("set_module", [], null, "Production module global/game selection adapter is not linked."),
        new("import_opt_config", [], nameof(OptiScalerPresets), ""),
        new("update_opt_config", [], nameof(OptiScalerPresets), ""),
        new("set_opt_config", ["apply_opt_config", "select_opt_config"], "Linked LauncherActionRunner.OptConfig + real profile files", "AppConfig database persistence is an environment port; real DB is not opened."),
        new("set_opt_dll", [], null, "AppConfig injection-name adapter is not linked."),
        new("import_xxmi", [], null, "Production per-game mod directory lease is not exercised."),
        new("switch_dll", [], null, "Production DLL family resolution/download/selection is not exercised."),
        new("set_inject_delay", [], null, "AppConfig delay adapter is not linked."),
        new("set_game_setting", [], null, "Production game settings persistence is not exercised."),
        new("set_window_mode", ["force_window_mode"], null, "Real game registry writes are forbidden in this fixture."),
        new("override_files", [], nameof(GameAddonPackUserContent), ""),
        new("copy_file", ["place_file"], nameof(PackFilePlacement), ""),
        new("import_shader", [], nameof(PackLocalActionExecutor), ""),
        new("set_shader", [], nameof(PackLocalActionExecutor), ""),
        new("delete_shader", [], nameof(PackLocalActionExecutor), ""),
        new("run_compat_check", [], null, "Environment/driver checks are outside this fixture."),
        new("add_game_from_registry", [], null, "Real installed-game registry access is outside this fixture."),
        new("launch_game", ["launch", "start_game"], null, "Starting games is forbidden in this offline fixture."),
        new("stop_game", ["close_game", "kill_game", "stopgame"], null, "Stopping games is forbidden in this offline fixture."),
        new("wait", ["sleep", "delay"], nameof(PackLocalActionExecutor), ""),
        new("click_game", ["click_window"], null, "Real input/window actions are forbidden in this fixture."),
        new("capture_game", [], null, "Real game capture is forbidden in this fixture."),
        new("activate_game", [], null, "Real window activation is forbidden in this fixture."),
        new("press_key", ["send_key"], null, "Real input actions are forbidden in this fixture."),
        new("test_game", ["launch_wait_stop", "self_test"], null, "Starting/stopping games is forbidden in this fixture."),
        new("test_map", ["map_test"], null, "Starting games and sending input is forbidden in this fixture."),
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 1 ? args[1]
            : Path.Combine(Path.GetTempPath(), "F6-pack-lifecycle-20261115", "run-" + Guid.NewGuid().ToString("N")));
        string source = Path.GetFullPath(args.Length > 2 ? args[2] : FindSourceRoot());
        Directory.CreateDirectory(output);
        string fixtures = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var checks = new List<CheckResult>();
        var matrix = new List<ActionResult>();
        string? oldTemporary = TemporaryFolder.Override;
        string oldOptiRoot = AppConfig.OptiScalerRootPath;
        var oldValues = AppConfig.Values.ToArray();
        TemporaryFolder.Override = Path.Combine(fixtures, "temporary");
        AppConfig.OptiScalerRootPath = Path.Combine(fixtures, "preset-library");
        AppConfig.Values.Clear();
        async Task Case(string id, Func<Task> test)
        {
            var timer = Stopwatch.StartNew();
            try { await test(); checks.Add(new(id, "PASS", "", timer.ElapsedMilliseconds)); }
            catch (Exception ex) { checks.Add(new(id, "FAIL", ex.ToString(), timer.ElapsedMilliseconds)); }
            Console.WriteLine($"{checks[^1].Status} {id} ({timer.ElapsedMilliseconds} ms)"
                + (checks[^1].Status == "FAIL" ? ": " + checks[^1].Detail.Split('\n')[0] : ""));
        }
        try
        {
            await Case("action-inventory-matches-production", () =>
            {
                string runner = File.ReadAllText(Path.Combine(source, "src", "HoYoShadeHub", "Features", "Plugins", "LauncherActionRunner.cs"));
                int start = runner.IndexOf("private static async Task<string> DispatchStepAsync", StringComparison.Ordinal);
                int end = runner.IndexOf("private static GameId? ResolveGameId", start, StringComparison.Ordinal);
                string body = runner[start..end];
                var actual = Regex.Matches(body, "\"([a-z_0-9]+)\"(?=\\s*(?:or|=>))")
                    .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                var expected = Contracts.SelectMany(c => c.Aliases.Prepend(c.Name)).ToHashSet(StringComparer.Ordinal);
                Require(actual.SetEquals(expected), "Dispatcher/matrix mismatch. Missing tests: "
                    + string.Join(',', actual.Except(expected)) + "; stale tests: " + string.Join(',', expected.Except(actual)));
                return Task.CompletedTask;
            });
            await Case("first-install-auto-actions-all-three-games", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "first"));
                await f.ImportAsync(f.Package("A", "1", "Package"));
                foreach (string game in Games)
                {
                    Require(!File.Exists(f.Entry(game).ReShadeIniPath), "Installer must not reach into a real or implicit game INI.");
                    await f.ApplyAsync(game);
                    Require(f.Value(game) == "Package", game + " missing explicit first-install values.");
                    Require(f.Value(game, "USER", "Keep") == "yes", "Unrelated template key lost.");
                    Require(State(f.Pack(game)).Values.All(value => value == "success"), "Completed INI not committed as success.");
                }
            });
            await Case("gamepack-only-overlay-can-install-declared-actions", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "gamepack-only"));
                await f.ImportAsync(f.Package("ActionsOnly", "1", "Package", includePayload: false));
                await f.ApplyAsync("hk4e_cn");
                Require(f.Value("hk4e_cn") == "Package", "GamePack-only overlay did not apply its declared actions.");
            });
            await Case("unapplied-import-survives-next-launcher-startup", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "deferred-import"));
                await f.ImportAsync(f.Package("Deferred", "1", "Package"));
                // User deferred action confirmation, then restarted the launcher.
                // This is the exact shared cleanup used by Program before GUI creation.
                PackAutoStartupCleanup.DeleteExisting(Path.Combine(f.Cache, "games"));
                Require(PackAutoActionSnapshot.TryLoad(f.Pack("hk4e_cn")) is not null,
                    "Startup cleanup deleted newly imported, never-applied actions; they cannot be confirmed or applied later.");
                await f.ApplyAsync("hk4e_cn");
            });
            await Case("repeat-start-preserves-user-ini-and-once-ledger", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "repeat"));
                await f.ImportAsync(f.Package("A", "1", "Package"));
                foreach (string game in Games)
                {
                    await f.ApplyAsync(game);
                    GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                    f.Edit(game, "User-" + game);
                    string before = Hash(f.Entry(game).ReShadeIniPath!);
                    string ledger = Hash(Path.Combine(f.Pack(game), PackOnceLedger.FileName));
                    for (int launch = 0; launch < 3; launch++)
                    {
                        await f.ApplyAsync(game);
                        GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                    }
                    Require(f.Value(game) == "User-" + game && Hash(f.Entry(game).ReShadeIniPath!) == before,
                        game + " user INI was changed by a repeat startup.");
                    Require(Hash(Path.Combine(f.Pack(game), PackOnceLedger.FileName)) == ledger,
                        "Read/skip changed completed once ledger.");
                }
            });
            foreach (string game in Games)
                await Case("legacy-ini-config-is-first-apply-only-" + game, () =>
                {
                    var f = new Fixture(Path.Combine(fixtures, "legacy-" + game));
                    f.Bind(game);
                    File.WriteAllText(Path.Combine(f.Pack(game), "ini_config.json"),
                        JsonSerializer.Serialize(new { set = new { CONTRACT = new { Value = "Package" } } }));
                    GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                    Require(f.Value(game) == "Package", "Legacy first apply missing.");
                    f.Edit(game, "UserSaved");
                    GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                    Require(f.Value(game) == "UserSaved", game + " ini_config.json overwrote a user-saved INI on the next startup.");
                    return Task.CompletedTask;
                });
            await Case("package-preset-user-edits-survive-repeat-start", () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "preset-defaults"));
                const string game = "nap_cn";
                f.Bind(game);
                Write(Path.Combine(f.Pack(game), "presets", "UserPreset.ini"), "[CONTRACT]\nValue=Package\n");
                GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                string imported = Path.Combine(f.Addons(game), Dlss5PresetLibrary.PresetFolderName, "UserPreset.ini");
                Require(File.Exists(imported), "Package preset was not imported.");
                File.WriteAllText(imported, "[CONTRACT]\nValue=UserSaved\n");
                GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                Require(IniDocument.Load(imported).GetValue("CONTRACT", "Value") == "UserSaved",
                    "Repeated bootstrap replaced the edited package preset.");
                return Task.CompletedTask;
            });
            foreach (string mode in new[] { "identical-package", "new-version-same-identities", "different-package-new-plan" })
                await Case("explicit-reimport-" + mode, async () =>
                {
                    var f = new Fixture(Path.Combine(fixtures, mode));
                    string first = f.Package("A", "1", "Package-A");
                    await f.ImportAsync(first);
                    foreach (string game in Games) { await f.ApplyAsync(game); f.Edit(game, "UserSaved"); }
                    string next = mode switch
                    {
                        "identical-package" => first,
                        "new-version-same-identities" => f.Package("A", "2", "Package-A2"),
                        _ => f.Package("B", "1", "Package-B", extraStep: true),
                    };
                    await f.ImportAsync(next);
                    foreach (string game in Games)
                    {
                        Require(!File.Exists(Path.Combine(f.Pack(game), PackOnceLedger.FileName)), "Import callback did not reset old batch.");
                        await f.ApplyAsync(game);
                        string expected = mode == "identical-package" ? "Package-A" : mode == "new-version-same-identities" ? "Package-A2" : "Package-B";
                        Require(f.Value(game) == expected, "Old once ledger blocked explicit " + mode + ".");
                        if (mode == "different-package-new-plan") Require(f.Value(game, "CONTRACT", "Extra") == "B", "New plan did not run added step.");
                        f.Edit(game, "NewUserSaved");
                        await f.ApplyAsync(game);
                        Require(f.Value(game) == "NewUserSaved", "Reimport remains repeatable after its first application.");
                    }
                });
            foreach (string mode in new[] { "identical-package", "new-version", "different-package" })
                await Case("defaults-and-presets-explicit-batch-" + mode, async () =>
                {
                    var f = new Fixture(Path.Combine(fixtures, "defaults-batch-" + mode));
                    string first = f.Package("DefaultsA", "1", "DefaultA", includeDefaults: true);
                    await f.ImportAsync(first);
                    var identities = new Dictionary<string, string>();
                    foreach (string game in Games)
                    {
                        f.Bind(game);
                        GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                        identities[game] = PackApplicationBatch.ReadIdentity(f.Pack(game));
                        Require(f.Value(game) == "DefaultA", "First ini_config missing.");
                        f.Edit(game, "UserSaved");
                        Write(Path.Combine(f.Addons(game), Dlss5PresetLibrary.PresetFolderName, "Default.ini"), "[CONTRACT]\nValue=UserPreset\n");
                        GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                        Require(f.Value(game) == "UserSaved", "Ordinary bootstrap replayed ini_config.");
                    }
                    string next = mode == "identical-package" ? first
                        : f.Package(mode == "new-version" ? "DefaultsA" : "DefaultsB", "2", "DefaultB", includeDefaults: true);
                    await f.ImportAsync(next);
                    string wanted = mode == "identical-package" ? "DefaultA" : "DefaultB";
                    foreach (string game in Games)
                    {
                        Require(PackApplicationBatch.ReadIdentity(f.Pack(game)) != identities[game], "Explicit import reused old batch identity.");
                        f.Bind(game);
                        GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                        string preset = Path.Combine(f.Addons(game), Dlss5PresetLibrary.PresetFolderName, "Default.ini");
                        Require(f.Value(game) == wanted && IniDocument.Load(preset).GetValue("CONTRACT", "Value") == wanted,
                            "New batch did not reapply INI and preset defaults.");
                        f.Edit(game, "AfterImport");
                        Write(preset, "[CONTRACT]\nValue=AfterImportPreset\n");
                        GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                        Require(f.Value(game) == "AfterImport" && IniDocument.Load(preset).GetValue("CONTRACT", "Value") == "AfterImportPreset",
                            "New batch did not become once-only after success.");
                    }
                });
            await Case("identical-reimport-invalidates-pre-import-snapshot", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "snapshot-batch"));
                string zip = f.Package("Same", "1", "Package");
                await f.ImportAsync(zip);
                var old = PackAutoActionSnapshot.TryLoad(f.Pack(Games[0]))!;
                await f.ImportAsync(zip);
                int calls = 0;
                var rejected = await PackActionOncePolicy.RunSnapshotAsync(old.LoadActions().Single(), old,
                    f.Pack(Games[0]), () => true, _ => { calls++; return Task.FromResult("Should not run"); });
                Require(rejected.Blocked && calls == 0, "An old same-hash snapshot crossed into a new import batch.");
                Require(!File.Exists(Path.Combine(f.Pack(Games[0]), PackOnceLedger.FileName)), "Rejected snapshot registered old plan.");
            });
            await Case("failed-preset-stays-pending-and-real-retry-succeeds", () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "preset-retry"));
                const string game = "hk4e_cn";
                f.Bind(game);
                Write(Path.Combine(f.Pack(game), "presets", "Default.ini"), "[CONTRACT]\nValue=Package\n");
                string target = Path.Combine(f.Addons(game), Dlss5PresetLibrary.PresetFolderName, "Default.ini");
                Write(target, "[CONTRACT]\nValue=User\n");
                using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Require(GameAddonPackUserContent.SyncPresets(f.Pack(game), f.Addons(game)) == 0, "Locked preset reported imported.");
                Require(ApplicationStates(f.Pack(game)).Values.Single() == "pending", "Failed preset was marked success.");
                Require(GameAddonPackUserContent.SyncPresets(f.Pack(game), f.Addons(game)) == 1, "Pending preset did not retry.");
                Require(ApplicationStates(f.Pack(game)).Values.Single() == "success", "Successful retry not committed.");
                Write(target, "[CONTRACT]\nValue=UserAfterRetry\n");
                Require(GameAddonPackUserContent.SyncPresets(f.Pack(game), f.Addons(game)) == 0
                    && IniDocument.Load(target).GetValue("CONTRACT", "Value") == "UserAfterRetry", "Completed retry overwrote user edit.");
                return Task.CompletedTask;
            });
            await Case("failed-default-ini-stays-pending-and-bootstrap-retries", () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "default-ini-retry"));
                const string game = "hkrpg_cn";
                f.Bind(game);
                Write(Path.Combine(f.Pack(game), "ini_config.json"), "{\"set\":{\"CONTRACT\":{\"Value\":\"Package\"}}}");
                var config = GamePackIniConfig.Load(f.Pack(game))!;
                bool failed = false;
                using (File.Open(f.Entry(game).ReShadeIniPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (var batch = PackApplicationBatch.Open(f.Pack(game)))
                    try { batch.ApplyOnce("ini_config", () => config.ApplyStrict(f.Entry(game).ReShadeIniPath!)); }
                    catch (IOException) { failed = true; }
                Require(failed && ApplicationStates(f.Pack(game))["ini_config"] == "pending", "Failed INI default reported success.");
                GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                Require(f.Value(game) == "Package" && ApplicationStates(f.Pack(game))["ini_config"] == "success", "Bootstrap did not finish failed default.");
                return Task.CompletedTask;
            });
            await Case("corrupt-application-state-preserves-user-config", () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "corrupt-apply"));
                const string game = "nap_cn";
                f.Bind(game);
                Write(Path.Combine(f.Pack(game), "ini_config.json"), "{\"set\":{\"CONTRACT\":{\"Value\":\"Package\"}}}");
                GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                f.Edit(game, "UserSaved");
                Write(Path.Combine(f.Pack(game), PackApplicationBatch.FileName), "{}");
                GameIniBootstrap.Ensure(f.Entry(game), f.Host);
                Require(f.Value(game) == "UserSaved" && File.ReadAllText(Path.Combine(f.Pack(game), PackApplicationBatch.FileName)) == "{}",
                    "Corrupt batch was silently reset and defaults replayed.");
                return Task.CompletedTask;
            });
            await Case("preset-import-detaches-existing-hardlink", () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "preset-hardlink"));
                const string game = "nap_cn";
                f.Bind(game);
                string peer = Path.Combine(f.Root, "user-shared.ini");
                Write(peer, "[CONTRACT]\nValue=UserShared\n");
                string target = Path.Combine(f.Addons(game), Dlss5PresetLibrary.PresetFolderName, "Default.ini");
                Require(HysxFileLink.LinkOrCopy(peer, target), "Fixture expected a same-volume hardlink.");
                Write(Path.Combine(f.Pack(game), "presets", "Default.ini"), "[CONTRACT]\nValue=Package\n");
                Require(GameAddonPackUserContent.SyncPresets(f.Pack(game), f.Addons(game)) == 1, "Preset missing.");
                Require(!HysxFileLink.AreHardLinked(peer, target) && IniDocument.Load(peer).GetValue("CONTRACT", "Value") == "UserShared",
                    "Preset import wrote through an existing hardlink.");
                return Task.CompletedTask;
            });
            await Case("locked-gamepack-import-retains-batch-and-completed-plan", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "import-lock"));
                const string game = "hk4e_cn";
                string first = f.Package("A", "1", "A", onlyGame: game);
                await f.ImportAsync(first);
                await f.ApplyAsync(game);
                f.Edit(game, "UserSaved");
                string batch = PackApplicationBatch.ReadIdentity(f.Pack(game));
                string ledger = Hash(Path.Combine(f.Pack(game), PackOnceLedger.FileName));
                bool failed = false;
                using (File.Open(Path.Combine(f.Pack(game), "auto.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    try { await f.ImportAsync(f.Package("B", "2", "B", onlyGame: game)); }
                    catch (IOException) { failed = true; }
                Require(failed && batch == PackApplicationBatch.ReadIdentity(f.Pack(game))
                    && ledger == Hash(Path.Combine(f.Pack(game), PackOnceLedger.FileName)), "Failed transaction advanced batch or discarded plan.");
                await f.ApplyAsync(game);
                Require(f.Value(game) == "UserSaved", "Failed import replayed old defaults.");
                await f.ImportAsync(first);
                await f.ApplyAsync(game);
                Require(f.Value(game) == "A", "Import could not retry after locked destination.");
            });
            await Case("incoming-application-ledger-cannot-spoof-batch", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "batch-injection"));
                string package = f.Package("Bad", "1", "Bad");
                using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
                    ZipText(zip, "GamePack/hk4e_cn/" + PackApplicationBatch.FileName, "{}");
                bool rejected = false;
                try { await f.ImportAsync(package); } catch (InvalidDataException) { rejected = true; }
                Require(rejected && PackApplicationBatch.ReadIdentity(f.Pack("hk4e_cn")) == "", "Untrusted archive supplied its own local batch.");
            });
            await Case("failed-import-does-not-reset-or-replay-completed-actions", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "failed-import"));
                await f.ImportAsync(f.Package("A", "1", "Package"));
                foreach (string game in Games) { await f.ApplyAsync(game); f.Edit(game, "UserSaved"); }
                var before = Games.ToDictionary(game => game, game => Hash(Path.Combine(f.Pack(game), PackOnceLedger.FileName)));
                string bad = Path.Combine(f.Root, "bad.zip");
                File.WriteAllText(bad, "not a ZIP");
                bool rejected = false;
                try { await f.ImportAsync(bad); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Invalid ZIP was accepted.");
                foreach (string game in Games)
                {
                    Require(Hash(Path.Combine(f.Pack(game), PackOnceLedger.FileName)) == before[game], "Failed import reset once history.");
                    await f.ApplyAsync(game);
                    Require(f.Value(game) == "UserSaved", "Failed import caused setup replay.");
                }
            });
            await Case("ini-failure-remains-pending-explicit-retry-resumes-prefix", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "pending-ini"));
                const string game = "hk4e_cn";
                f.Bind(game);
                string auto = """{"actions":[{"name":"retry","steps":[{"action":"set_ini_keys","file":"Prefix.ini","set":{"TEST":{"Value":"prefix"}}},{"action":"set_ini_keys","set":{"CONTRACT":{"Value":"fixed"}}},{"action":"set_ini_keys","file":"After.ini","set":{"TEST":{"Value":"after"}}}]}]}""";
                File.WriteAllText(Path.Combine(f.Pack(game), "auto.json"), auto);
                var snapshot = PackAutoActionSnapshot.TryLoad(f.Pack(game))!;
                var action = snapshot.LoadActions().Single();
                int prefixCalls = 0;
                Task<string> Dispatch(PackActionStep step)
                {
                    if (step.GetString("file") == "Prefix.ini") prefixCalls++;
                    PackIniAction.Apply(step, f.Entry(game), f.Host);
                    return Task.FromResult("Applied INI");
                }
                using (File.Open(f.Entry(game).ReShadeIniPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var failed = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch);
                    Require(failed.Blocked, "Locked target reported success.");
                }
                var states = State(f.Pack(game));
                Require(states[PackActionOncePolicy.StableKey(action.Name, 0, action.Steps[0])] == "success"
                    && states[PackActionOncePolicy.StableKey(action.Name, 1, action.Steps[1])] == "pending"
                    && !File.Exists(Path.Combine(f.Entry(game).GameDirectory!, "After.ini")), "Failure committed success or ran tail.");
                var automatic = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch);
                Require(automatic.Reason == PackActionOncePolicy.BlockReason.Pending && f.Value(game) != "fixed", "Pending INI retried without confirmation.");
                var retried = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch,
                    confirmPendingIniRetry: _ => Task.FromResult(true));
                Require(!retried.Blocked && prefixCalls == 1 && f.Value(game) == "fixed"
                    && File.Exists(Path.Combine(f.Entry(game).GameDirectory!, "After.ini")), "Confirmed retry did not resume pending INI correctly.");
            });
            await Case("file-placement-failure-not-success-reimport-can-retry", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "pending-copy"));
                const string game = "nap_cn";
                f.Bind(game);
                Write(Path.Combine(f.Pack(game), "payload.ini"), "new");
                Write(Path.Combine(f.Entry(game).GameDirectory!, "Config", "payload.ini"), "old");
                File.WriteAllText(Path.Combine(f.Pack(game), "auto.json"), """{"steps":[{"action":"copy_file","file":"payload.ini","directory":"Config","target":"game"}]}""");
                var snapshot = PackAutoActionSnapshot.TryLoad(f.Pack(game))!;
                var action = snapshot.LoadActions().Single();
                int calls = 0;
                Task<string> Dispatch(PackActionStep step)
                {
                    calls++;
                    PackFilePlacement.Apply(PackFilePlacement.Resolve(step, f.Pack(game), f.Entry(game).GameDirectory,
                        Path.Combine(f.Root, "launcher"), f.Host.RootPath), f.Pack(game));
                    return Task.FromResult("Placed file");
                }
                string destination = Path.Combine(f.Entry(game).GameDirectory!, "Config", "payload.ini");
                using (File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var failed = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch);
                    Require(failed.Blocked && State(f.Pack(game)).Values.Single() == "pending", "Failed copy incorrectly marked success.");
                }
                var blocked = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch);
                Require(blocked.Reason == PackActionOncePolicy.BlockReason.Pending && calls == 1 && File.ReadAllText(destination) == "old", "Partial file operation automatically replayed.");
                Require(PackOnceLedger.ResetForReimport(f.Pack(game)), "Explicit reimport did not release failed batch.");
                var recovered = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch);
                Require(!recovered.Blocked && calls == 2 && File.ReadAllText(destination) == "new", "Reimport cannot retry failed file operation.");
            });
            await Case("per-step-consent-and-whole-action-disable", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "selection"));
                const string game = "hkrpg_cn";
                f.Bind(game);
                File.WriteAllText(Path.Combine(f.Pack(game), "auto.json"), """{"actions":[{"name":"picked|原始","steps":[{"action":"set_ini_keys","set":{"CONTRACT":{"Value":"declined"}}},{"action":"set_ini_keys","set":{"CONTRACT":{"Checked":"yes"}}}]}]}""");
                var snapshot = PackAutoActionSnapshot.TryLoad(f.Pack(game))!;
                var action = snapshot.LoadActions().Single();
                var selection = new PackStepSelection(new(StringComparer.Ordinal), new([PackStepSelection.Key(action, 0)], StringComparer.Ordinal));
                var saved = PackConsentRecord.Parse(PackConsentRecord.Serialize(snapshot.Hash, [], selection.DisabledSteps))!.Selection;
                int calls = 0;
                Task<string> Dispatch(PackActionStep step) { calls++; PackIniAction.Apply(step, f.Entry(game), f.Host); return Task.FromResult("INI applied"); }
                var result = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch, isStepEnabled: saved.IsEnabled);
                Require(!result.Blocked && calls == 1 && f.Value(game) != "declined" && f.Value(game, "CONTRACT", "Checked") == "yes", "Per-step selection not honored.");
                Require(!State(f.Pack(game)).ContainsKey(PackActionOncePolicy.StableKey(action.Name, 0, action.Steps[0])), "Unchecked step got a ledger entry.");
                var disabled = new PackStepSelection(new([action.Name], StringComparer.Ordinal), new(StringComparer.Ordinal));
                result = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch, isStepEnabled: disabled.IsEnabled);
                Require(!result.Blocked && calls == 1, "Disabled action reached side effects.");
                result = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, f.Pack(game), () => true, Dispatch);
                Require(!result.Blocked && calls == 2 && f.Value(game) == "declined", "Explicit later approval cannot run previously unchecked step.");
            });
            await Case("three-game-isolation-across-single-game-reimport", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "isolation"));
                await f.ImportAsync(f.Package("A", "1", "A"));
                foreach (string game in Games) { await f.ApplyAsync(game); f.Edit(game, "User-" + game); }
                string[] protectedPaths = Games.Skip(1).SelectMany(game => new[] { f.Entry(game).ReShadeIniPath!, Path.Combine(f.Pack(game), PackOnceLedger.FileName), Path.Combine(f.Pack(game), "auto.json") }).ToArray();
                string[] before = protectedPaths.Select(Hash).ToArray();
                await f.ImportAsync(f.Package("OnlyGenshin", "2", "B", onlyGame: "hk4e_cn"));
                await f.ApplyAsync("hk4e_cn");
                Require(f.Value("hk4e_cn") == "B", "Target game did not apply.");
                Require(before.SequenceEqual(protectedPaths.Select(Hash)), "Other game's INI, actions or ledger changed.");
            });
            await Case("opti-user-profile-survives-start-and-can-be-explicitly-reapplied", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "opti-profile"));
                string preset = "Contract-" + Guid.NewGuid().ToString("N");
                string build = Path.Combine(f.Root, "opti", "official", "F6", "v5");
                Write(Path.Combine(build, "OptiScaler.ini"), "[NR]\nQuality=2\n");
                Require(OptiScalerPresets.Save(preset, "[NR]\nQuality=3\n"), "Cannot create fixture preset.");
                const string game = "hk4e_cn";
                string pack = f.Pack(game);
                File.WriteAllText(Path.Combine(pack, "auto.json"), JsonSerializer.Serialize(new { steps = new[] { new { action = "set_opt_config", name = preset } } }));
                var snapshot = PackAutoActionSnapshot.TryLoad(pack)!;
                var action = snapshot.LoadActions().Single();
                Task<string> Apply(PackActionStep _) => Task.FromResult(OptiScalerPresets.Apply(build, game, preset) ? "Applied profile" : "✗ profile failed");
                var first = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true, Apply);
                Require(!first.Blocked, first.Summary);
                string profile = OptiScalerProfiles.ProfilePath(build, game);
                File.WriteAllText(Path.Combine(build, "OptiScaler.ini"), "[NR]\nQuality=4\n");
                Require(OptiScalerProfiles.Store(build, game), "Cannot collect fixture runtime Save Settings.");
                for (int i = 0; i < 2; i++)
                {
                    await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true, Apply);
                    Require(OptiScalerProfiles.Activate(build, game), "Cannot activate saved fixture profile.");
                }
                Require(IniDocument.Load(profile).GetValue("NR", "Quality")?.Trim() == "4", "Once action overwrote runtime-saved profile.");
                PackOnceLedger.ResetForReimport(pack);
                var reapplied = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true, Apply);
                Require(!reapplied.Blocked && IniDocument.Load(profile).GetValue("NR", "Quality")?.Trim() == "3", "Explicit reimport cannot reapply Opti preset.");
            });
            await Case("malformed-snapshot-and-unauthorized-context-have-no-side-effects", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "bad-snapshot"));
                string pack = f.Pack("nap_cn");
                foreach (string json in new[] { "{", "{\"actions\":[]}", "{\"steps\":[{\"set\":{}}]}", "{\"steps\":[{\"action\":\"set_ini_keys\"},42]}" })
                {
                    File.WriteAllText(Path.Combine(pack, "auto.json"), json);
                    Require(PackAutoActionSnapshot.TryLoad(pack) is null, "Malformed snapshot authorized partial execution.");
                }
                File.WriteAllText(Path.Combine(pack, "auto.json"), """{"steps":[{"action":"set_ini_keys","set":{"CONTRACT":{"Value":"bad"}}}]}""");
                var snapshot = PackAutoActionSnapshot.TryLoad(pack)!;
                var action = snapshot.LoadActions().Single();
                int calls = 0;
                var result = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => false,
                    _ => { calls++; return Task.FromResult("Should not run"); });
                Require(result.Blocked && calls == 0 && !File.Exists(Path.Combine(pack, PackOnceLedger.FileName)), "Invalid context created execution history.");
            });
            foreach (string alias in new[] { "set_opt_config", "apply_opt_config", "select_opt_config" })
                foreach (string game in Games)
                    await Case("actual-opt-if-missing-" + alias + "-" + game, () =>
                    {
                        var f = new Fixture(Path.Combine(fixtures, "opt-if-missing-" + alias + "-" + game));
                        string build = f.PrepareOptEnvironment(game);
                        File.Delete(Path.Combine(build, OptiScalerRuntime.ConfigFileName));
                        Require(OptiScalerPresets.Save("First", "[NR]\nQuality=3\n"), "Cannot save first preset.");
                        var step = Step(JsonSerializer.Serialize(new { action = alias, name = "First", if_missing = true }));
                        string result = LauncherActionRunner.ExecuteOptConfigFixture(step, f.OptContext(game));
                        Require(PackActionOncePolicy.IsSuccessfulOutcome(result), result);
                        string profile = OptiScalerProfiles.ProfilePath(build, game);
                        Require(File.Exists(profile) && File.Exists(Path.Combine(build, OptiScalerRuntime.ConfigFileName))
                            && IniDocument.Load(profile).GetValue("NR", "Quality")?.Trim() == "3", "if_missing did not create and activate first profile.");
                        Write(Path.Combine(build, OptiScalerRuntime.ConfigFileName), "[NR]\nQuality=4\n");
                        Require(OptiScalerProfiles.Store(build, game), "Could not collect fixture Save Settings.");
                        string before = Hash(profile);
                        var savedConfig = AppConfig.Values.ToDictionary();
                        result = LauncherActionRunner.ExecuteOptConfigFixture(step, f.OptContext(game));
                        Require(PackActionOncePolicy.IsSuccessfulOutcome(result) && result.Contains("if_missing")
                            && before == Hash(profile) && savedConfig.OrderBy(x => x.Key).SequenceEqual(AppConfig.Values.OrderBy(x => x.Key)),
                            "if_missing replaced saved profile or selected another preset.");
                        return Task.CompletedTask;
                    });
            await Case("actual-opt-invalid-and-locked-config-never-success", () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "opt-error-handlers"));
                const string game = "nap_cn";
                f.PrepareOptEnvironment(game);
                string missing = LauncherActionRunner.ExecuteOptConfigFixture(Step("{\"action\":\"set_opt_config\",\"name\":\"missing\"}"), f.OptContext(game));
                Require(!PackActionOncePolicy.IsSuccessfulOutcome(missing), "Missing Opt preset reported success.");
                using (File.Open(Path.Combine(f.Pack(game), "Opti.ini"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    string locked = LauncherActionRunner.ExecuteOptConfigFixture(Step("{\"action\":\"import_opt_config\",\"file\":\"Opti.ini\",\"name\":\"Locked\"}"), f.OptContext(game));
                    Require(!PackActionOncePolicy.IsSuccessfulOutcome(locked), "Locked Opt preset reported success.");
                }
                return Task.CompletedTask;
            });
            await Case("actual-local-invalid-inputs-and-failures-not-success", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "actual-local-failures"));
                const string game = "hk4e_cn";
                f.Bind(game);
                var context = f.LocalContext(game);
                foreach (string json in new[]
                {
                    "{\"action\":\"require_launcher_version\",\"minVersion\":\"999.0\"}",
                    "{\"action\":\"request_launcher_version\",\"version\":\"invalid\"}",
                    "{\"action\":\"wait\",\"ms\":-1}", "{\"action\":\"delay\",\"seconds\":3601}",
                    "{\"action\":\"sleep\"}", "{\"action\":\"set_addons\"}",
                    "{\"action\":\"import_shader\",\"file\":\"missing.fx\"}",
                }) Require(!PackActionOncePolicy.IsSuccessfulOutcome(await PackLocalActionExecutor.ExecuteAsync(Step(json), context)), "Invalid local action reported success: " + json);
                using (File.Open(f.Entry(game).ReShadeIniPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Require(!PackActionOncePolicy.IsSuccessfulOutcome(await PackLocalActionExecutor.ExecuteAsync(Step("{\"action\":\"clear_game_ini\"}"), context)),
                        "Locked INI deletion was reported successful.");
            });
            foreach (string target in new[] { "launcher", "shade" })
                await Case("actual-file-placement-" + target, async () =>
                {
                    var f = new Fixture(Path.Combine(fixtures, "place-" + target));
                    const string game = "nap_cn";
                    f.Bind(game);
                    var context = f.LocalContext(game);
                    string json = JsonSerializer.Serialize(new { action = "place_file", file = "Payload.ini", directory = "Config", target, name = "Installed.ini" });
                    string note = await PackLocalActionExecutor.ExecuteAsync(Step(json), context);
                    string root = target == "launcher" ? context.LauncherDirectory! : f.Host.RootPath;
                    Require(PackActionOncePolicy.IsSuccessfulOutcome(note) && File.ReadAllText(Path.Combine(root, "Config", "Installed.ini")) == File.ReadAllText(Path.Combine(f.Pack(game), "Payload.ini")), note);
                });
            await Case("actual-overlay-launcher-and-shader-toggle", async () =>
            {
                var f = new Fixture(Path.Combine(fixtures, "actual-launcher-overlay"));
                const string game = "nap_cn";
                f.Bind(game);
                var context = f.LocalContext(game);
                Write(Path.Combine(f.Pack(game), "launcher_files", "Nested", "Config.ini"), "[TEST]\nValue=Overlay\n");
                string result = await PackLocalActionExecutor.ExecuteAsync(Step("{\"action\":\"override_files\",\"target\":\"launcher\"}"), context);
                Require(PackActionOncePolicy.IsSuccessfulOutcome(result) && File.Exists(Path.Combine(context.LauncherDirectory!, "Nested", "Config.ini")), result);
                string preset = Path.Combine(f.Root, "active.ini");
                Write(preset, "Techniques=Keep@Keep.fx\nTechniqueSorting=Keep@Keep.fx\n");
                var profile = ReShadeProfile.Load(f.Entry(game).ReShadeIniPath!);
                profile.SetValue("GENERAL", "PresetPath", preset); profile.Save();
                var enable = Step("{\"action\":\"set_shader\",\"name\":\"Fixture@Fixture.fx\",\"enabled\":true}");
                result = await PackLocalActionExecutor.ExecuteAsync(enable, context);
                string before = Hash(preset);
                Require(PackActionOncePolicy.IsSuccessfulOutcome(result)
                    && File.ReadAllText(preset).Contains("Fixture@Fixture.fx"), result);
                await PackLocalActionExecutor.ExecuteAsync(enable, context);
                Require(before == Hash(preset), "Repeated enable duplicated technique or changed sorting.");
            });
            foreach (var contract in Contracts)
                foreach (string name in contract.Aliases.Prepend(contract.Name))
                    await Case("action-policy-" + name, async () =>
                    {
                        var f = new Fixture(Path.Combine(fixtures, "action-" + name));
                        const string game = "nap_cn";
                        f.Bind(game);
                        string pack = f.Pack(game);
                        var raw = Payload(name);
                        File.WriteAllText(Path.Combine(pack, "auto.json"), JsonSerializer.Serialize(new { actions = new[] { new { name = "matrix", runOnLaunch = true, steps = new[] { raw } } } }));
                        var snapshot = PackAutoActionSnapshot.TryLoad(pack) ?? throw new InvalidOperationException("Cannot parse fixture action " + name);
                        var action = snapshot.LoadActions().Single();
                        Require(action.Steps.Single().Action == name && action.RunOnLaunch, "Parse changed action or launch flag.");
                        int calls = 0;
                        Task<string> Dispatch(PackActionStep step)
                        {
                            calls++;
                            return contract.Service is null ? Task.FromResult("Policy boundary reached; effect unverified")
                                : ProbeRealServiceAsync(contract.Name, step, f, game);
                        }
                        var disabled = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true, Dispatch, isStepEnabled: (_, _) => false);
                        Require(!disabled.Blocked && calls == 0 && State(pack).Count == 0, "Disabled action reserved or executed.");
                        var first = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true, Dispatch);
                        Require(!first.Blocked && calls == 1 && State(pack).Values.Single() == "success", first.Summary);
                        var repeat = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, pack, () => true, Dispatch);
                        Require(!repeat.Blocked && calls == 1, "Completed action replayed.");
                        matrix.Add(new(name, contract.Name, true, true, true, true, contract.Service is not null,
                            contract.Service ?? "PackAutoActionSnapshot + PackStepSelection + PackActionOncePolicy + PackOnceLedger only", contract.Gap));
                    });
        }
        finally
        {
            TemporaryFolder.Override = oldTemporary;
            AppConfig.OptiScalerRootPath = oldOptiRoot;
            AppConfig.Values.Clear();
            foreach (var value in oldValues) AppConfig.Values[value.Key] = value.Value;
        }
        int failed = checks.Count(check => check.Status == "FAIL");
        int gaps = matrix.Count(action => action.Gap.Length > 0 || !action.RealServiceExecuted);
        var report = new
        {
            schema = 1, generatedUtc = DateTimeOffset.UtcNow, sourceRoot = source, fixturesRoot = fixtures,
            passed = checks.Count - failed, failed, actionCount = matrix.Count, actionEffectGaps = gaps,
            allProductionActionEffectsVerified = gaps == 0 && matrix.Count == Contracts.Sum(contract => contract.Aliases.Length + 1),
            gameTested = false, guiTested = false,
            importCallback = "Real installer commits application batch and clears prior once ledger; offline adapter then PlaceMatching -> Bind -> consent -> RunSnapshotAsync. WinUI callback itself is not executed.",
            checks, actions = matrix,
        };
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, Json));
        File.WriteAllText(Path.Combine(output, "actions.md"), "| Action | Real service | Policy/once/disable | Gap |\n|---|---|---|---|\n"
            + string.Join('\n', matrix.Select(action => $"| `{action.Action}` | {(action.RealServiceExecuted ? action.Evidence : "No")} | PASS | {action.Gap} |")) + "\n");
        Console.WriteLine($"Pack lifecycle: PASS {checks.Count - failed}, FAIL {failed}; actions {matrix.Count}; effect gaps {gaps}; GameTested=false; GuiTested=false.");
        Console.WriteLine("Results: " + Path.Combine(output, "results.json"));
        // Nonzero until both the behavioral contract and production-dispatch coverage are complete.
        return failed > 0 ? 1 : gaps > 0 ? 2 : 0;
    }

    private static object Payload(string name) => new
    {
        action = name, enabled = false, minVersion = "1.4.7", milliseconds = 0,
        file = name is "set_ini_keys" or "set_ini" or "write_ini" ? "ReShade.ini"
            : name is "import_opt_config" ? "Opti.ini" : name is "import_shader" ? "Fixture.fx" : "Payload.ini",
        name = name is "apply_preset" ? "Payload.ini" : name is "set_shader" ? "Fixture@Fixture.fx"
            : name is "delete_shader" ? "Fixture.fx" : "FixturePreset",
        directory = "Config", target = "game", folder = "game_files",
        set = new { CONTRACT = new { Value = "Matrix" } },
    };

    private static async Task<string> ProbeRealServiceAsync(string canonical, PackActionStep step, Fixture f, string game)
    {
        string pack = f.Pack(game);
        if (canonical is "import_opt_config" or "update_opt_config" or "set_opt_config")
        {
            string build = f.PrepareOptEnvironment(game);
            if (canonical == "set_opt_config") Require(OptiScalerPresets.Save("FixturePreset", "[NR]\nQuality=3\n"), "Opti preset save failed.");
            string result = LauncherActionRunner.ExecuteOptConfigFixture(step, f.OptContext(game));
            Require(PackActionOncePolicy.IsSuccessfulOutcome(result), result);
            if (canonical == "set_opt_config")
            {
                Require(IniDocument.Load(OptiScalerProfiles.ProfilePath(build, game)).GetValue("NR", "Quality")?.Trim() == "3", "Opti profile effect missing.");
                Require(AppConfig.Values[OptiScalerPresets.FollowKey(game)] == "FixturePreset", "Handler did not request selected-preset persistence.");
            }
            else Require(OptiScalerPresets.Read("FixturePreset")?.Contains(canonical == "import_opt_config" ? "Quality=3" : "Quality=2") == true, "Opti import/capture bytes missing.");
            return result;
        }
        var context = f.LocalContext(game);
        if (canonical == "apply_preset")
            Write(Path.Combine(f.Addons(game), Dlss5PresetLibrary.PresetFolderName, "Payload.ini"), "[TEST]\nValue=preset\n");
        if (canonical == "import_shader") Write(Path.Combine(pack, "Fixture.fx"), "// fixture shader text, never compiled or loaded");
        if (canonical is "set_shader" or "delete_shader")
        {
            string preset = Path.Combine(f.Root, "active-preset.ini");
            Write(preset, "Techniques=Fixture@Fixture.fx,Keep@Keep.fx\nTechniqueSorting=Fixture@Fixture.fx,Keep@Keep.fx\n");
            var profile = ReShadeProfile.Load(f.Entry(game).ReShadeIniPath!);
            profile.SetValue("GENERAL", "PresetPath", preset);
            profile.Save();
            Write(Path.Combine(f.Host.RootPath, "reshade-shaders", "Fixture.fx"), "// fixture, never compiled");
        }
        if (canonical == "clear_game_ini") Write(Path.Combine(f.Entry(game).GameDirectory!, GameIniBootstrap.SecondaryIniFileName), "[TEST]\nValue=secondary\n");
        string note = await PackLocalActionExecutor.ExecuteAsync(step, context);
        Require(PackActionOncePolicy.IsSuccessfulOutcome(note), note);
        switch (canonical)
        {
            case "set_ini_keys": Require(f.Value(game) == "Matrix", "INI effect missing."); break;
            case "set_addons": Require(context.Plugins!.GetAddons().Single(a => a.FileName == "Fixture.addon64").Enabled == false, "Addon disable missing."); break;
            case "import_preset": Require(File.Exists(Path.Combine(f.Addons(game), Dlss5PresetLibrary.PresetFolderName, "Payload.ini")), "Preset import missing."); break;
            case "apply_preset": Require(Path.GetFileName(ReShadeProfile.Load(f.Entry(game).ReShadeIniPath!).ResolvePresetPath()) == "Payload.ini", "Preset selection missing."); break;
            case "clear_game_ini": Require(!File.Exists(f.Entry(game).ReShadeIniPath) && !File.Exists(Path.Combine(f.Entry(game).GameDirectory!, GameIniBootstrap.SecondaryIniFileName)), "INI deletion incomplete."); break;
            case "copy_file": Require(File.ReadAllText(Path.Combine(f.Entry(game).GameDirectory!, "Config", "FixturePreset")) == File.ReadAllText(Path.Combine(pack, "Payload.ini")), "Placed bytes differ."); break;
            case "override_files": Require(File.ReadAllText(Path.Combine(f.Entry(game).GameDirectory!, "overlay.txt")) == "payload", "Overlay bytes missing."); break;
            case "import_shader": Require(File.ReadAllText(Path.Combine(f.Host.RootPath, "reshade-shaders", "Fixture.fx")).Contains("fixture shader"), "Shader import missing."); break;
            case "set_shader": Require(!IniDocument.Load(Path.Combine(f.Root, "active-preset.ini")).GetValue(IniDocument.RootSection, "Techniques")!.Contains("Fixture@"), "Technique still enabled."); break;
            case "delete_shader": Require(!File.Exists(Path.Combine(f.Host.RootPath, "reshade-shaders", "Fixture.fx")) && !File.ReadAllText(Path.Combine(f.Root, "active-preset.ini")).Split('\n')[0].Contains("Fixture@"), "Shader deletion incomplete."); break;
            case "require_launcher_version": Require(note.Contains("符合要求"), "Version validator did not pass."); break;
            case "wait": Require(note.Contains("0 ms"), "Wait duration not interpreted."); break;
            default: throw new InvalidOperationException("Missing actual handler probe: " + canonical);
        }
        return note;
    }

    private sealed class Fixture
    {
        public string Root { get; }
        public string Cache => Path.Combine(Root, "cache");
        public ShadeHost Host { get; }
        private readonly Dictionary<string, GameEntry> _entries = new(StringComparer.Ordinal);
        public Fixture(string root)
        {
            Root = root;
            Host = new ShadeHost(Path.Combine(root, "shade"));
            Write(Path.Combine(Host.RootPath, "ReShade64.dll"), "Transport fixture, never loaded");
            Write(Host.ReShadeIniPath, "; fixture template\n[USER]\nKeep=yes\n");
            Write(Path.Combine(Host.AddonsPath, "Fixture.addon64"), "Transport fixture, never loaded");
            foreach (string game in Games)
            {
                Directory.CreateDirectory(Pack(game));
                string directory = Path.Combine(root, "games", game);
                Directory.CreateDirectory(directory);
                _entries[game] = new("biz:" + game, game)
                {
                    ExePath = Path.Combine(directory, game.StartsWith("hk4e", StringComparison.Ordinal) ? "YuanShen.exe" : game.StartsWith("hkrpg", StringComparison.Ordinal) ? "StarRail.exe" : "ZenlessZoneZero.exe"),
                };
                Write(Path.Combine(Pack(game), "Payload.ini"), "[TEST]\nValue=payload\n");
                Write(Path.Combine(Pack(game), "Opti.ini"), "[NR]\nQuality=3\n");
                Write(Path.Combine(Pack(game), "game_files", "overlay.txt"), "payload");
            }
        }
        public PackLocalActionExecutor.Context LocalContext(string game) => new()
        {
            Entry = Entry(game), Host = Host, Plugins = new GamePluginService(Entry(game), Host),
            PackRoot = Pack(game), LauncherDirectory = Path.Combine(Root, "launcher"), LauncherVersion = "1.4.8.2",
        };
        public LauncherActionContext OptContext(string game) => new()
        {
            GameId = GameId.FromGameBiz(game), Entry = Entry(game), Host = Host,
            PackRoot = Pack(game),
        };
        public string PrepareOptEnvironment(string game)
        {
            string opti = Path.Combine(Root, "opti");
            string build = Path.Combine(opti, "local", "fixture");
            AppConfig.OptiScalerRootPath = opti;
            Write(Path.Combine(build, "OptiScaler.dll"), "fixture DLL, never loaded");
            Write(Path.Combine(build, "OptiScaler.ini"), "[NR]\nQuality=2\n");
            AppConfig.Values["fixture_build_" + game] = "local/fixture";
            AppConfig.Values["fixture_dll_" + game] = Path.Combine(build, "OptiScaler.dll");
            return build;
        }
        public GameEntry Entry(string game) => _entries[game];
        public string Pack(string game) => GameAddonPack.PackDirectory(Cache, game);
        public string Addons(string game) => GameAddonPack.AddonDirectory(Cache, game);
        public void Bind(string game)
        {
            GameAddonPack.Sync(Addons(game), GameAddonPack.Plan(Host.AddonsPath, null, null), []);
            GamePackIniBinding.Bind(Entry(game), Host, Addons(game));
        }
        public string? Value(string game, string section = "CONTRACT", string key = "Value")
            => IniDocument.Load(Entry(game).ReShadeIniPath!).GetValue(section, key)?.Trim();
        public void Edit(string game, string value)
        {
            var ini = IniDocument.Load(Entry(game).ReShadeIniPath!);
            ini.SetValue("CONTRACT", "Value", value);
            ini.Save(Entry(game).ReShadeIniPath!);
        }
        public string Package(string name, string version, string value, bool extraStep = false, string? onlyGame = null,
            bool includePayload = true, bool includeDefaults = false)
        {
            string path = Path.Combine(Root, name + "-" + version + "-" + Guid.NewGuid().ToString("N") + ".zip");
            string[] games = onlyGame is null ? Games : [onlyGame];
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            ZipText(zip, "filelist.json", JsonSerializer.Serialize(new
            {
                hysxOverlay = 1, name, version,
                gamePacks = games.Select(game => new { game, path = "GamePack/" + game }).ToArray(),
                targets = includePayload ? new[] { new { from = "Modules", to = "modules" } } : [],
            }));
            if (includePayload) ZipText(zip, "Modules/Fixture/README.txt", "Offline transport fixture; no native code is loaded.");
            foreach (string game in games)
            {
                var steps = new List<object> { new { action = "set_ini_keys", set = new { CONTRACT = new { Value = value } } } };
                if (extraStep) steps.Add(new { action = "set_ini_keys", set = new { CONTRACT = new { Extra = "B" } } });
                ZipText(zip, "GamePack/" + game + "/auto.json", JsonSerializer.Serialize(new { actions = new[] { new { name = name == "B" ? "other-package" : "setup", runOnLaunch = true, steps } } }));
                if (includeDefaults)
                {
                    ZipText(zip, "GamePack/" + game + "/ini_config.json", JsonSerializer.Serialize(new { set = new { CONTRACT = new { Value = value } } }));
                    ZipText(zip, "GamePack/" + game + "/presets/Default.ini", "[CONTRACT]\nValue=" + value + "\n");
                }
            }
            return path;
        }
        public async Task ImportAsync(string path)
        {
            var result = await new LocalPackageInstaller(Path.Combine(Root, "opti"), Host.AddonsPath,
                Path.Combine(Root, "modules"), shadeRoot: Host.RootPath, cacheRoot: Cache).InstallAsync(path, LocalPackageKind.Overlay);
            Require(result.GamePackReplaced, "Successful installer did not signal GamePack replacement.");
            string[] hints = (result.GamePackGameHints ?? (result.GamePackGameHint is { } hint ? [hint] : [])).ToArray();
            Require(hints.Length > 0, "Installer did not report replacement game hints.");
            // Installer commits new batch + clears old actions atomically. No test-only ledger reset.
            foreach (string game in Games.Where(game => hints.Any(hint => game.StartsWith(hint, StringComparison.OrdinalIgnoreCase))))
            {
                PackPendingMigration.PlaceMatching(Cache, game);
            }
        }
        public async Task ApplyAsync(string game)
        {
            Bind(game);
            var snapshot = PackAutoActionSnapshot.TryLoad(Pack(game)) ?? throw new InvalidOperationException("Installed actions unavailable.");
            var consent = PackConsentRecord.Parse(PackConsentRecord.Serialize(snapshot.Hash, [], []))!;
            foreach (var action in snapshot.LoadActions())
            {
                GameLaunchIniPreparation.EnsurePackActionTarget(Entry(game), Host, [action], consent.Selection.IsEnabled);
                var result = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, Pack(game), () => true, step =>
                {
                    PackIniAction.Apply(step, Entry(game), Host);
                    return Task.FromResult("INI applied");
                }, isStepEnabled: consent.Selection.IsEnabled);
                Require(!result.Blocked, result.Summary);
            }
        }
    }

    private static Dictionary<string, string> State(string pack)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, PackOnceLedger.FileName)));
        return document.RootElement.GetProperty("states").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }
    private static Dictionary<string, string> ApplicationStates(string pack)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, PackApplicationBatch.FileName)));
        return document.RootElement.GetProperty("Operations").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static PackActionStep Step(string json) => PackAutoActionFile.Parse("{\"steps\":[" + json + "]}").Single().Steps.Single();
    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void ZipText(ZipArchive zip, string path, string text)
    { using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(text); }
    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "HoYoShadeHub.Extensions.Tests", "HoYoShadeHub.Extensions.Tests.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Pass a source root as the third pack-lifecycle-tests argument.");
    }
}
