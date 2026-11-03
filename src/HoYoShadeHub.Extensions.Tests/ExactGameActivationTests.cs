using System.Text.Json;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class ExactGameActivationTests
{
    private const string Image = @"D:\Games\YuanShen.exe";
    private const long Created = 134377430398371062;
    private sealed class Target : IExactGameClickTarget
    {
        public int Pid { get; set; } = 43024;
        public long CreationFileTime { get; set; } = Created;
        public string ImagePath { get; set; } = Image;
        public bool HasExited { get; set; }
        public nint ForegroundWindow { get; set; } = 5678;
        public int Owner { get; set; } = 43024;
        public bool Visible { get; set; } = true;
        public bool Disposed { get; private set; }
        public int Inputs { get; private set; }
        public int WindowProcessId(nint _) => Owner;
        public bool IsTopLevelVisible(nint _) => Visible;
        public (int Width, int Height) ClientSize(nint _) => (1920, 1080);
        public (int X, int Y) ToScreen(nint _, int x, int y) => (x, y);
        public void SetCursor(int _, int __) => Inputs++;
        public void ClickOnce() => Inputs++;
        public void Dispose() => Disposed = true;
    }
    private sealed class Window(Target target) : IExactGameActivationWindow
    {
        public IReadOnlyList<nint> Handles { get; set; } = [1234];
        public bool TopLevel { get; set; } = true;
        public bool Unowned { get; set; } = true;
        public string ClassName { get; set; } = "UnityWndClass";
        public bool Cloaked { get; set; }
        public bool Minimized { get; set; }
        public bool Accept { get; set; } = true;
        public bool ShowAccepted { get; set; } = true;
        public bool RemainHidden { get; set; }
        public int Requests { get; private set; }
        public int Restores { get; private set; }
        public int Shows { get; private set; }
        public Action? DuringRequest { get; set; }
        public Action? DuringShow { get; set; }
        public IReadOnlyList<nint> EnumerateWindows() => Handles;
        public bool IsTopLevel(nint _) => TopLevel;
        public bool IsUnowned(nint _) => Unowned;
        public string WindowClass(nint _) => ClassName;
        public bool IsCloaked(nint _) => Cloaked;
        public bool IsMinimized(nint _) => Minimized;
        public bool Show(nint _)
        {
            Shows++;
            if (ShowAccepted && !RemainHidden) target.Visible = true;
            DuringShow?.Invoke();
            return ShowAccepted;
        }
        public bool Restore(nint _)
        {
            Restores++;
            if (ShowAccepted) { Minimized = false; if (!RemainHidden) target.Visible = true; }
            DuringShow?.Invoke();
            return ShowAccepted;
        }
        public bool RequestForeground(nint window)
        {
            Requests++;
            if (Accept) target.ForegroundWindow = window;
            DuringRequest?.Invoke();
            return Accept;
        }
    }

    public static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool ok, string name)
        { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
        JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        var raw = Json("""{"action":"activate_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062,"expectedWindow":1234}""");
        var request = ExactGameActivationRequest.Parse(raw);
        var action = new PackAutoAction();
        action.Steps.Add(new PackActionStep { Action = "activate_game", Raw = raw });
        Check(ExactGameActivationRequest.ValidateCliBatch("hk4e_cn", [action]) == request, "exact single-step activation parsed");
        var recovery = ExactGameActivationRequest.Parse(Json(raw.GetRawText().Replace(
            "\"expectedWindow\":1234", "\"expectedWindow\":1234,\"restoreHiddenWindow\":true")));
        Check(recovery.RestoreHiddenWindow && !request.RestoreHiddenWindow, "hidden recovery requires explicit boolean authorization");
        foreach (string invalid in new[] { "null", "1", "\"true\"", "{}" })
        {
            bool rejected = false;
            try { ExactGameActivationRequest.Parse(Json(raw.GetRawText().Replace(
                "\"expectedWindow\":1234", "\"expectedWindow\":1234,\"restoreHiddenWindow\":" + invalid))); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "non-boolean hidden recovery rejected: " + invalid);
        }
        foreach (string invalid in new[] { "0", "-1", "1234.0", "1e3", "\"1234\"" })
        {
            bool rejected = false;
            try { ExactGameActivationRequest.Parse(Json(raw.GetRawText().Replace("\"expectedWindow\":1234", "\"expectedWindow\":" + invalid))); }
            catch { rejected = true; }
            Check(rejected, "invalid HWND rejected: " + invalid);
        }
        bool denied = false;
        try { ExactGameActivationRequest.ValidateCliBatch("hkrpg_cn", [action]); } catch { denied = true; }
        Check(denied, "other game rejected");
        action.Steps.Add(new PackActionStep { Action = "capture_game", Raw = raw });
        denied = false;
        try { ExactGameActivationRequest.ValidateCliBatch("hk4e_cn", [action]); } catch { denied = true; }
        Check(denied, "activation plus capture batch rejected");

        async Task Reject(Target target, Window window, string label, int expectedRequests = 0)
        {
            bool rejected = false;
            try { await ExactGameActivation.ExecuteAsync(request, Image, _ => target, window, () => Task.CompletedTask); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && target.Disposed && target.Inputs == 0 && window.Requests == expectedRequests, label);
        }
        var reused = new Target { CreationFileTime = Created + 1 };
        await Reject(reused, new Window(reused), "PID reuse rejected before activation");
        var path = new Target { ImagePath = @"D:\Other\YuanShen.exe" };
        await Reject(path, new Window(path), "wrong executable path rejected");
        var foreign = new Target { Owner = 99 };
        await Reject(foreign, new Window(foreign), "foreign HWND rejected");
        var cloaked = new Target();
        await Reject(cloaked, new Window(cloaked) { Cloaked = true }, "cloaked window rejected");
        var blocked = new Target();
        await Reject(blocked, new Window(blocked) { Accept = false }, "Windows foreground denial preserved", 1);
        var exited = new Target();
        await Reject(exited, new Window(exited) { DuringRequest = () => exited.HasExited = true },
            "process exit during activation rejected", 1);
        var good = new Target();
        var operations = new Window(good) { Minimized = true };
        string result = await ExactGameActivation.ExecuteAsync(request, Image, _ => good, operations, () => Task.CompletedTask);
        Check(good.Disposed && good.Inputs == 0 && operations.Restores == 1 && operations.Requests == 1
            && good.ForegroundWindow == request.Window && result.Contains("verified"), "one restore and one verified activation without input");

        async Task RejectRecovery(Target target, Window window, string label, int shows = 0, int requests = 0)
        {
            bool rejected = false;
            try { await ExactGameActivation.ExecuteAsync(recovery, Image, _ => target, window, () => Task.CompletedTask); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && target.Disposed && target.Inputs == 0 && window.Shows == shows
                && window.Requests == requests, label);
        }
        var unauthorized = new Target { Visible = false };
        var unauthorizedWindows = new Window(unauthorized);
        await Reject(unauthorized, unauthorizedWindows, "hidden window without authorization rejected");
        Check(unauthorizedWindows.Shows == 0 && unauthorizedWindows.Restores == 0, "unauthorized recovery has no show or restore side effect");
        var hidden = new Target { Visible = false };
        var hiddenWindows = new Window(hidden);
        await ExactGameActivation.ExecuteAsync(recovery, Image, _ => hidden, hiddenWindows, () => Task.CompletedTask);
        Check(hidden.Visible && hidden.Disposed && hidden.Inputs == 0 && hiddenWindows.Shows == 1
            && hiddenWindows.Restores == 0 && hiddenWindows.Requests == 1, "unique hidden main window shown once then verified foreground without input");
        var hiddenMinimized = new Target { Visible = false };
        var minimizedWindows = new Window(hiddenMinimized) { Minimized = true };
        await ExactGameActivation.ExecuteAsync(recovery, Image, _ => hiddenMinimized, minimizedWindows, () => Task.CompletedTask);
        Check(hiddenMinimized.Visible && hiddenMinimized.Disposed && hiddenMinimized.Inputs == 0
            && minimizedWindows.Restores == 1 && minimizedWindows.Shows == 0 && minimizedWindows.Requests == 1,
            "hidden minimized main window uses one restore instead of separate show");
        var multiple = new Target { Visible = false };
        await RejectRecovery(multiple, new Window(multiple) { Handles = [1234, 9999] }, "ambiguous main windows rejected before show");
        var missing = new Target { Visible = false };
        await RejectRecovery(missing, new Window(missing) { Handles = [] }, "missing main window rejected before show");
        var unknown = new Target { Visible = false };
        await RejectRecovery(unknown, new Window(unknown) { ClassName = "UnknownHelperWindow" }, "unknown hidden window class rejected");
        var owned = new Target { Visible = false };
        await RejectRecovery(owned, new Window(owned) { Unowned = false }, "owned hidden utility window rejected");
        var child = new Target { Visible = false };
        await RejectRecovery(child, new Window(child) { TopLevel = false }, "non-top-level hidden window rejected");
        var hiddenForeign = new Target { Visible = false, Owner = 99 };
        await RejectRecovery(hiddenForeign, new Window(hiddenForeign), "foreign hidden window rejected");
        var hiddenReused = new Target { Visible = false, CreationFileTime = Created + 1 };
        await RejectRecovery(hiddenReused, new Window(hiddenReused), "reused PID rejected before hidden recovery");
        var hiddenWrongImage = new Target { Visible = false, ImagePath = @"D:\Other\YuanShen.exe" };
        await RejectRecovery(hiddenWrongImage, new Window(hiddenWrongImage), "wrong executable rejected before hidden recovery");
        var hiddenCloaked = new Target { Visible = false };
        await RejectRecovery(hiddenCloaked, new Window(hiddenCloaked) { Cloaked = true }, "cloaked hidden window is not shown");
        var showDenied = new Target { Visible = false };
        await RejectRecovery(showDenied, new Window(showDenied) { ShowAccepted = false }, "show request denial stops before foreground", shows: 1);
        var stillHidden = new Target { Visible = false };
        await RejectRecovery(stillHidden, new Window(stillHidden) { RemainHidden = true }, "visibility rechecked after accepted show", shows: 1);
        var exitedOnShow = new Target { Visible = false };
        await RejectRecovery(exitedOnShow, new Window(exitedOnShow) { DuringShow = () => exitedOnShow.HasExited = true },
            "process exit during show stops before foreground", shows: 1);
        var replacedOnShow = new Target { Visible = false };
        await RejectRecovery(replacedOnShow, new Window(replacedOnShow) { DuringShow = () => replacedOnShow.Owner = 99 },
            "window ownership change during show stops before foreground", shows: 1);
        var changedClass = new Target { Visible = false };
        var changedClassWindows = new Window(changedClass);
        changedClassWindows.DuringShow = () => changedClassWindows.ClassName = "UnknownHelperWindow";
        await RejectRecovery(changedClass, changedClassWindows, "main-window class change during show rejected", shows: 1);
        var addedMain = new Target { Visible = false };
        var addedMainWindows = new Window(addedMain);
        addedMainWindows.DuringShow = () => addedMainWindows.Handles = [1234, 9999];
        await RejectRecovery(addedMain, addedMainWindows, "new ambiguous main window during show rejected", shows: 1);
        var deniedForeground = new Target { Visible = false };
        await RejectRecovery(deniedForeground, new Window(deniedForeground) { Accept = false },
            "showing a hidden main window never bypasses Windows foreground denial", shows: 1, requests: 1);
        Console.WriteLine($"Exact activation tests: PASS {passed}, FAIL 0 (fake windows only)");
        return 0;
    }
}
