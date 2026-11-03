using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Games;

public sealed record ExactGameActivationRequest(int Pid, long CreationFileTime, nint Window, bool RestoreHiddenWindow = false)
{
    public static ExactGameActivationRequest Parse(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object) throw new ArgumentException("activate_game requires an object step.");
        var identity = ExactGameCaptureRequest.Parse(step);
        if (!step.TryGetProperty("expectedWindow", out var value) || value.ValueKind != JsonValueKind.Number)
            throw new ArgumentException("expectedWindow must be a positive decimal HWND integer.");
        string raw = value.GetRawText();
        if (raw.Length == 0 || raw.Any(c => c is < '0' or > '9')
            || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long window) || window <= 0)
            throw new ArgumentException("expectedWindow must be a positive decimal HWND integer.");
        bool restoreHidden = false;
        if (step.TryGetProperty("restoreHiddenWindow", out var restore))
        {
            if (restore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("restoreHiddenWindow must be a JSON boolean.");
            restoreHidden = restore.GetBoolean();
        }
        return new(identity.Pid, identity.CreationFileTime, checked((nint)window), restoreHidden);
    }

    public static ExactGameActivationRequest ValidateCliBatch(string? biz, IReadOnlyList<PackAutoAction> actions)
    {
        if (!string.Equals(biz, "hk4e_cn", StringComparison.Ordinal)
            || actions.Count != 1 || actions[0].Steps.Count != 1
            || !string.Equals(actions[0].Steps[0].Action.Trim(), "activate_game", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("activate_game requires --biz hk4e_cn and exactly one action with one step.");
        return Parse(actions[0].Steps[0].Raw);
    }
}

public interface IExactGameActivationWindow
{
    IReadOnlyList<nint> EnumerateWindows();
    bool IsTopLevel(nint window);
    bool IsUnowned(nint window);
    string WindowClass(nint window);
    bool IsCloaked(nint window);
    bool IsMinimized(nint window);
    bool Show(nint window);
    bool Restore(nint window);
    bool RequestForeground(nint window);
}

public static class ExactGameActivation
{
    public static async Task<string> ExecuteAsync(ExactGameActivationRequest request, string expectedImagePath,
        Func<int, IExactGameClickTarget>? bind = null, IExactGameActivationWindow? windows = null,
        Func<Task>? settle = null)
    {
        using var target = (bind ?? ExactGameClick.BindTarget)(request.Pid);
        windows ??= new NativeWindow(request.Pid);
        settle ??= () => Task.Delay(200);
        void Verify(bool requireVisible)
        {
            if (string.IsNullOrWhiteSpace(expectedImagePath) || !Path.IsPathFullyQualified(expectedImagePath))
                throw new InvalidOperationException("Cannot verify the game's absolute executable path.");
            if (target.HasExited || target.Pid != request.Pid || target.CreationFileTime != request.CreationFileTime
                || !Path.GetFullPath(target.ImagePath).Equals(Path.GetFullPath(expectedImagePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Game PID, creation FILETIME or executable path does not match.");
            if (request.Window == 0 || target.WindowProcessId(request.Window) != request.Pid
                || !windows.IsTopLevel(request.Window))
                throw new InvalidOperationException("The expected HWND is invalid, foreign, or not a top-level window.");
            if (windows.IsCloaked(request.Window))
                throw new InvalidOperationException("The game window is cloaked or not on the visible desktop.");
            if (request.RestoreHiddenWindow)
            {
                // A title or a Process.MainWindowHandle hint cannot authorize revealing
                // arbitrary hidden helper windows. Require the unique Unity main window.
                var mainWindows = windows.EnumerateWindows().Where(window =>
                    target.WindowProcessId(window) == request.Pid && windows.IsTopLevel(window)
                    && windows.IsUnowned(window)
                    && string.Equals(windows.WindowClass(window), "UnityWndClass", StringComparison.Ordinal))
                    .Distinct().ToArray();
                if (mainWindows.Length != 1 || mainWindows[0] != request.Window)
                    throw new InvalidOperationException("Hidden-window recovery requires the unique unowned UnityWndClass game main window.");
            }
            if (requireVisible && !target.IsTopLevelVisible(request.Window))
                throw new InvalidOperationException("The game window is hidden; recovery requires explicit restoreHiddenWindow authorization and a unique verified main window.");
            var client = target.ClientSize(request.Window);
            if (client.Width <= 0 || client.Height <= 0)
                throw new InvalidOperationException("Game client area is unavailable.");
        }
        Verify(requireVisible: !request.RestoreHiddenWindow);
        bool wasHidden = !target.IsTopLevelVisible(request.Window);
        bool wasMinimized = windows.IsMinimized(request.Window);
        if (wasHidden || wasMinimized)
        {
            // Revalidate immediately before the only show/restore request. An explicit
            // authorization permits visibility recovery, never HWND/PID substitution.
            Verify(requireVisible: !request.RestoreHiddenWindow);
            if (!(wasMinimized ? windows.Restore(request.Window) : windows.Show(request.Window)))
                throw new InvalidOperationException("Windows rejected the game-window show/restore request.");
            await settle().ConfigureAwait(false);
            Verify(requireVisible: true);
            if (windows.IsMinimized(request.Window))
                throw new InvalidOperationException("The game window is still minimized.");
        }
        bool requested = false;
        if (target.ForegroundWindow != request.Window)
        {
            Verify(requireVisible: true);
            requested = windows.RequestForeground(request.Window);
            await settle().ConfigureAwait(false);
        }
        Verify(requireVisible: true);
        // Do not feed an unrelated foreground HWND into VerifyForeground and
        // misreport it as rejection of the requested game's visible window.
        nint foreground = target.ForegroundWindow;
        if (foreground != request.Window || windows.IsMinimized(request.Window))
            throw new InvalidOperationException(
                $"Game foreground activation was not retained: requestedWindow={request.Window.ToInt64()} " +
                $"actualForeground={foreground.ToInt64()} actualForegroundPid={target.WindowProcessId(foreground)} " +
                $"apiAccepted={requested}; target identity and visibility passed; no input was sent.");
        (nint Window, int Width, int Height) actual;
        try
        {
            actual = ExactGameClick.VerifyForeground(target, request.Pid, request.CreationFileTime, expectedImagePath);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Final foreground verification changed after target-window verification: requestedWindow={request.Window.ToInt64()} " +
                $"actualForeground={target.ForegroundWindow.ToInt64()} apiAccepted={requested}; no input was sent.", ex);
        }
        if (actual.Window != request.Window || windows.IsMinimized(request.Window))
            throw new InvalidOperationException("Windows did not grant foreground activation; no input was sent.");
        return $"activate_game verified: pid={request.Pid} filetime={request.CreationFileTime} window={request.Window.ToInt64()} wasHidden={wasHidden} wasMinimized={wasMinimized} apiAccepted={requested} client={actual.Width}x{actual.Height}; no input sent";
    }

    private sealed class NativeWindow : IExactGameActivationWindow
    {
        private readonly int _expectedPid;

        public NativeWindow(int expectedPid) => _expectedPid = expectedPid;

        public IReadOnlyList<nint> EnumerateWindows()
        {
            var windows = new List<nint>();
            EnumWindowProc callback = (window, _) => { windows.Add(window); return true; };
            if (!EnumWindows(callback, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate top-level windows.");
            return windows;
        }
        public bool IsTopLevel(nint window) => IsWindow(window) && GetAncestor(window, 2) == window;
        public bool IsUnowned(nint window) => GetWindow(window, 4) == 0;
        public string WindowClass(nint window)
        {
            var name = new StringBuilder(256);
            if (GetClassNameW(window, name, name.Capacity) == 0)
                throw new InvalidOperationException("Cannot verify the game-window class.");
            return name.ToString();
        }
        public bool IsCloaked(nint window)
        {
            int result = DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint));
            if (result != 0) throw new InvalidOperationException("Cannot verify game-window cloaking state.");
            return cloaked != 0;
        }
        public bool IsMinimized(nint window) => IsIconic(window);
        public bool Show(nint window) => ShowWindowAsync(window, 1);
        public bool Restore(nint window) => ShowWindowAsync(window, 9);
        public bool RequestForeground(nint window)
        {
            // Preserve the existing ordinary activation before any fallback metadata
            // or input-queue cooperation. Actual success is still checked by ExecuteAsync.
            bool accepted = SetForegroundWindow(window);
            nint foreground = GetForegroundWindow();
            if (accepted || foreground == window) return accepted;

            uint callerThread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(window, out uint targetPid);
            uint foregroundPid = 0;
            uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out foregroundPid);

            void VerifyTarget()
            {
                if (targetThread == 0 || targetPid != (uint)_expectedPid
                    || GetWindowThreadProcessId(window, out uint pid) != targetThread || pid != targetPid
                    || !IsTopLevel(window) || !IsWindowVisible(window) || IsMinimized(window) || IsCloaked(window))
                    throw new InvalidOperationException("The exact game window changed before foreground activation; no input was sent.");
            }
            bool ForegroundSnapshotMatches()
                => GetForegroundWindow() == foreground
                    && GetWindowThreadProcessId(foreground, out uint pid) == foregroundThread && pid == foregroundPid;

            // An unknown/changed owner or an unusable queue cannot replace the
            // original rejection with a new eligibility failure. Do not recapture/retry.
            if (foregroundThread == 0 || foregroundPid == 0 || foregroundThread == callerThread)
                return accepted;
            VerifyTarget();
            if (!ForegroundSnapshotMatches()) return accepted;

            // Initialize this CLI thread's message queue without removing a message
            // or sending keyboard/mouse input. AttachThreadInput requires queues.
            PeekMessageW(out _, new nint(-1), 0, 0, 0x0002); // PM_NOREMOVE | PM_NOYIELD
            VerifyTarget();
            if (!ForegroundSnapshotMatches()) return accepted;
            bool attached = false;
            try
            {
                // Attach itself enforces the OS same-desktop restriction; never
                // switch desktops or certify a desktop by its name/metadata.
                attached = AttachThreadInput(callerThread, foregroundThread, true);
                if (attached)
                {
                    // Cooperate only with the snapshotted foreground owner. Joining
                    // only the game thread does not supply foreground interaction.
                    VerifyTarget();
                    if (ForegroundSnapshotMatches())
                        accepted = SetForegroundWindow(window); // One fallback; at most two requests in total.
                }
                // Attach refusal preserves the ordinary false result. The existing
                // settle/final actual-HWND checks remain authoritative in either case.
            }
            finally
            {
                // This entire method is synchronous: detach the same native thread
                // before ExecuteAsync can await its existing settle/identity checks.
                // A failed detach must also be reported if target revalidation threw.
                if (attached && !AttachThreadInput(callerThread, foregroundThread, false))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                        "Cannot detach the foreground input queues; activation is not reported as successful.");
            }
            return accepted;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool PeekMessageW(out NativeMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    private delegate bool EnumWindowProc(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
}
