using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HoYoShadeHub.Extensions.ReShade;
using Microsoft.Win32.SafeHandles;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Single click on a precisely identified Genshin process; foreground is required by default.</summary>
public sealed record ExactGameClickRequest(int Pid, long CreationFileTime, double X, double Y)
{
    public nint ExpectedWindow { get; init; }
    public bool ActivationClick { get; init; }

    public static ExactGameClickRequest Parse(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object) throw new ArgumentException("click_game requires an object step.");
        int pid = checked((int)PositiveInteger(step, "expectedPid"));
        long creation = PositiveInteger(step, "expectedCreationFileTime");
        try { _ = DateTime.FromFileTimeUtc(creation); }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("expectedCreationFileTime is outside Windows FILETIME range."); }
        bool activationClick = false;
        if (step.TryGetProperty("activationClick", out JsonElement activation))
        {
            if (activation.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("activationClick must be a JSON boolean.");
            activationClick = activation.GetBoolean();
        }
        nint window = step.TryGetProperty("expectedWindow", out _)
            ? checked((nint)PositiveInteger(step, "expectedWindow")) : 0;
        if (activationClick && window == 0)
            throw new ArgumentException("activationClick requires an explicit expectedWindow.");
        return new(pid, creation, Normalized(step, "x"), Normalized(step, "y"))
        { ExpectedWindow = window, ActivationClick = activationClick };
    }

    public static ExactGameClickRequest ValidateCliBatch(string? biz, IReadOnlyList<PackAutoAction> actions)
    {
        if (!string.Equals(biz, "hk4e_cn", StringComparison.Ordinal))
            throw new ArgumentException("click_game is restricted to --biz hk4e_cn.");
        if (actions.Count != 1 || actions[0].Steps.Count != 1
            || !string.Equals(actions[0].Steps[0].Action.Trim(), "click_game", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("click_game must be the only step in the only action of this CLI call.");
        return Parse(actions[0].Steps[0].Raw);
    }

    private static long PositiveInteger(JsonElement step, string name)
    {
        if (!step.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            throw new ArgumentException(name + " must be a positive JSON integer.");
        string raw = value.GetRawText();
        if (raw.Length == 0 || raw.Any(c => c is < '0' or > '9')
            || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0)
            throw new ArgumentException(name + " must be a positive decimal integer.");
        return number;
    }

    private static double Normalized(JsonElement step, string name)
    {
        if (!step.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double n) || !double.IsFinite(n) || n < 0 || n >= 1)
            throw new ArgumentException(name + " must be a finite normalized coordinate in [0, 1).");
        return n;
    }
}

public interface IExactGameClickTarget : IDisposable
{
    int Pid { get; }
    long CreationFileTime { get; }
    string ImagePath { get; }
    bool HasExited { get; }
    nint ForegroundWindow { get; }
    int WindowProcessId(nint window);
    bool IsTopLevelVisible(nint window);
    (int Width, int Height) ClientSize(nint window);
    (int X, int Y) ToScreen(nint window, int x, int y);
    void SetCursor(int x, int y);
    void ClickOnce();
}

/// <summary>Optional exact-hit capability; activation clicks fail closed without it.</summary>
public interface IExactGameActivationClickTarget : IExactGameClickTarget
{
    bool IsMinimized(nint window);
    bool IsCloaked(nint window);
    nint RootWindowAtPoint(int x, int y);
    (int X, int Y) CursorPosition { get; }
    // Existing targets without a raise capability must fail closed, never skip the hit check.
    void RaiseWithoutActivation(nint window) => throw new InvalidOperationException(
        "Non-activating game-window raise is unavailable; no button input was sent.");
}

public static class ExactGameClick
{
    /// <summary>Read-only binding for a separately authorized foreground-window operation.</summary>
    public static IExactGameClickTarget BindTarget(int pid) => new NativeTarget(pid);

    public static (nint Window, int Width, int Height) VerifyForeground(
        IExactGameClickTarget target, int pid, long creationFileTime, string expectedImagePath)
    {
        nint window = target.ForegroundWindow;
        VerifyWindowIdentity(target, pid, creationFileTime, expectedImagePath, window);
        (int width, int height) = target.ClientSize(window);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Game client area is unavailable.");
        return (window, width, height);
    }

    public static void VerifyWindowIdentity(IExactGameClickTarget target, int pid,
        long creationFileTime, string expectedImagePath, nint window)
    {
        if (string.IsNullOrWhiteSpace(expectedImagePath) || !Path.IsPathFullyQualified(expectedImagePath))
            throw new InvalidOperationException("Cannot verify the game's absolute executable path.");
        if (target.HasExited || target.Pid != pid || target.CreationFileTime != creationFileTime
            || !Path.GetFullPath(target.ImagePath).Equals(Path.GetFullPath(expectedImagePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Game PID, creation FILETIME or executable path does not match.");
        if (window == 0 || target.WindowProcessId(window) != pid || !target.IsTopLevelVisible(window))
            throw new InvalidOperationException("The expected game window is not a visible top-level window of the bound process.");
    }

    public static string Execute(ExactGameClickRequest request, string expectedImagePath,
        Func<int, IExactGameClickTarget>? bind = null)
    {
        if (string.IsNullOrWhiteSpace(expectedImagePath) || !Path.IsPathFullyQualified(expectedImagePath))
            throw new InvalidOperationException("Cannot verify the game's absolute executable path.");
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y)
            || request.X < 0 || request.X >= 1 || request.Y < 0 || request.Y >= 1)
            throw new ArgumentException("Click coordinates must be finite normalized values in [0, 1).");
        if (request.ActivationClick && request.ExpectedWindow <= 0)
            throw new ArgumentException("activationClick requires an explicit expectedWindow.");
        using IExactGameClickTarget target = (bind ?? (pid => new NativeTarget(pid)))(request.Pid);
        VerifyIdentity(target, request, expectedImagePath);
        nint window = request.ActivationClick ? request.ExpectedWindow : target.ForegroundWindow;
        if (request.ExpectedWindow != 0 && window != request.ExpectedWindow)
            throw new InvalidOperationException("The expected HWND is not the actual foreground window.");
        VerifyWindowIdentity(target, request.Pid, request.CreationFileTime, expectedImagePath, window);
        IExactGameActivationClickTarget? hitTarget = request.ActivationClick
            ? target as IExactGameActivationClickTarget
                ?? throw new InvalidOperationException("Exact-hit verification is unavailable; no button input was sent.")
            : null;
        (int width, int height) = target.ClientSize(window);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Game client area is unavailable.");
        int x = (int)Math.Floor(request.X * width), y = (int)Math.Floor(request.Y * height);
        (int screenX, int screenY) = target.ToScreen(window, x, y);
        void VerifyActivationGeometry()
        {
            if (hitTarget!.IsMinimized(window) || hitTarget.IsCloaked(window))
                throw new InvalidOperationException("Activation-click target is minimized or cloaked; no button input was sent.");
            var client = target.ClientSize(window);
            if (client.Width != width || client.Height != height || x < 0 || y < 0
                || x >= client.Width || y >= client.Height
                || target.ToScreen(window, x, y) != (screenX, screenY))
                throw new InvalidOperationException("Activation-click client geometry changed; no button input was sent.");
        }
        void VerifyBeforeClick(bool requireCursor)
        {
            VerifyWindowIdentity(target, request.Pid, request.CreationFileTime, expectedImagePath, window);
            if (!request.ActivationClick)
            {
                if (target.ForegroundWindow != window)
                    throw new InvalidOperationException("Game window lost focus before click; no button input was sent.");
                return;
            }
            VerifyActivationGeometry();
            if (requireCursor && hitTarget!.CursorPosition != (screenX, screenY))
                throw new InvalidOperationException("Cursor is not at the exact authorized point; no button input was sent.");
            // This is the last check before SendInput, including after SetCursorPos.
            // A visible target behind another root window never authorizes input.
            if (hitTarget!.RootWindowAtPoint(screenX, screenY) != window)
                throw new InvalidOperationException("The exact click point is obscured or unknown; no button input was sent.");
        }
        bool raiseApiAccepted = false;
        if (hitTarget is not null && target.ForegroundWindow != window)
        {
            // Only the explicit activation-click path can raise this verified game HWND.
            // The entry helper authorizes it only after its exact no-input activation rejection.
            VerifyWindowIdentity(target, request.Pid, request.CreationFileTime, expectedImagePath, window);
            VerifyActivationGeometry();
            hitTarget.RaiseWithoutActivation(window); // One request; no activation or retry.
            raiseApiAccepted = true;
        }
        VerifyBeforeClick(requireCursor: false);
        target.SetCursor(screenX, screenY);
        VerifyBeforeClick(requireCursor: true);
        target.ClickOnce();
        try
        {
            // SendInput queues events; allow one bounded settle, never another click or a retry loop.
            if (request.ActivationClick) System.Threading.Thread.Sleep(200);
            var actual = VerifyForeground(target, request.Pid, request.CreationFileTime, expectedImagePath);
            if (actual.Window != window || (hitTarget is not null
                && (hitTarget.IsMinimized(window) || hitTarget.IsCloaked(window))))
                throw new InvalidOperationException("The expected HWND is not the usable actual foreground window.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "click_game SendInput accepted 2/2 left-button events, but post-click foreground verification failed: "
                + ex.Message + " Input was already sent; consumption/world entry UNKNOWN; do not click again.", ex);
        }
        return $"click_game sent once: pid={request.Pid} filetime={request.CreationFileTime} client=({x},{y}) screen=({screenX},{screenY}) window=0x{window.ToInt64():X} activationClick={request.ActivationClick} raiseApiAccepted={raiseApiAccepted} foregroundVerified=True; SendInput accepted 2/2 left-button events; consumption/world entry UNKNOWN";
    }

    private static void VerifyIdentity(IExactGameClickTarget target, ExactGameClickRequest request, string path)
    {
        if (target.HasExited || target.Pid != request.Pid || target.CreationFileTime != request.CreationFileTime
            || !Path.GetFullPath(target.ImagePath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Game PID, creation FILETIME or executable path does not match.");
    }

    private sealed class NativeTarget : IExactGameActivationClickTarget
    {
        private const uint ProcessAccess = 0x1000 | 0x00100000;
        private readonly SafeProcessHandle _handle;
        public int Pid { get; }
        public long CreationFileTime { get; }
        public string ImagePath { get; }
        public bool HasExited => WaitForSingleObject(_handle, 0) switch
        { 0 => true, 258 => false, _ => throw new Win32Exception(Marshal.GetLastWin32Error()) };
        public nint ForegroundWindow => GetForegroundWindow();

        public NativeTarget(int pid)
        {
            Pid = pid;
            _handle = OpenProcess(ProcessAccess, false, pid);
            if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!GetProcessTimes(_handle, out long created, out _, out _, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                CreationFileTime = created;
                uint size = 32768;
                var path = new StringBuilder((int)size);
                if (!QueryFullProcessImageNameW(_handle, 0, path, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                ImagePath = path.ToString();
            }
            catch { _handle.Dispose(); throw; }
        }
        public int WindowProcessId(nint window)
        { GetWindowThreadProcessId(window, out uint pid); return (int)pid; }
        public bool IsTopLevelVisible(nint window) => IsWindowVisible(window) && GetAncestor(window, 2) == window;
        public bool IsMinimized(nint window) => IsIconic(window);
        public bool IsCloaked(nint window)
        {
            if (DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint)) != 0)
                throw new InvalidOperationException("Cannot verify game-window cloaking state.");
            return cloaked != 0;
        }
        public nint RootWindowAtPoint(int x, int y)
        {
            nint hit = WindowFromPoint(new Point { X = x, Y = y });
            return hit == 0 ? 0 : GetAncestor(hit, 2);
        }
        public (int X, int Y) CursorPosition
        {
            get
            {
                if (!GetCursorPos(out Point point)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return (point.X, point.Y);
            }
        }
        public (int Width, int Height) ClientSize(nint window)
        {
            if (!GetClientRect(window, out Rect rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        public (int X, int Y) ToScreen(nint window, int x, int y)
        {
            var point = new Point { X = x, Y = y };
            if (!ClientToScreen(window, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (point.X, point.Y);
        }
        public void RaiseWithoutActivation(nint window)
        {
            // HWND_TOP changes only Z order; preserve position, size and owner Z order.
            // SWP_NOACTIVATE preserves activation; HWND_TOP leaves topmost style unchanged.
            const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
            if (!SetWindowPos(window, 0, 0, 0, 0, 0, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows rejected the non-activating game-window raise; no button input was sent.");
        }
        public void SetCursor(int x, int y)
        { if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void ClickOnce()
        {
            var inputs = new[] { new MouseInput { Type = 0, Data = new MouseData { Flags = 0x0002 } },
                new MouseInput { Type = 0, Data = new MouseData { Flags = 0x0004 } } };
            uint sent = SendInput(2, inputs, Marshal.SizeOf<MouseInput>());
            int error = sent == 2 ? 0 : Marshal.GetLastWin32Error();
            // Preserve the existing release-only recovery. Never resend the click.
            string recovery = "no release recovery needed";
            if (sent == 1)
            {
                uint released = SendInput(1, [inputs[1]], Marshal.SizeOf<MouseInput>());
                int releaseError = released == 1 ? 0 : Marshal.GetLastWin32Error();
                recovery = $"release-only recovery accepted {released}/1 events (win32Error={releaseError})";
            }
            if (sent != 2)
                throw new Win32Exception(error,
                    $"click_game SendInput accepted {sent}/2 left-button events (win32Error={error}); {recovery}. "
                    + "Input may be partial; foreground/consumption/world entry UNKNOWN; do not click again.");
        }
        public void Dispose() => _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public uint Type; public MouseData Data; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    { public int Dx, Dy; public uint MouseDataValue, Flags, Time; public nint ExtraInfo; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, int size);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, MouseInput[] inputs, int size);
}
