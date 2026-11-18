using System.Runtime.InteropServices;

namespace F6Launcher.Background;

/// <summary>A Win32 notification icon and message loop; no WinUI/WinForms/COM UI runtime.</summary>
internal static class NativeTray
{
    private const uint Callback = 0x8001, SetVisibility = 0x8002, QuitMessage = 0x8003;
    private static readonly WindowProc CallbackProc = WndProc;
    private static nint _window;
    private static bool _visible, _fixture;
    private static uint _taskbarCreated;
    private static Action? _open, _exit;

    internal static void Run(Action open, Action exit, bool fixture)
    {
        _open = open; _exit = exit; _fixture = fixture;
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = Marshal.GetFunctionPointerForDelegate(CallbackProc),
            Instance = GetModuleHandle(null), ClassName = "F6LauncherBackgroundHost" };
        if (RegisterClassEx(ref wc) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _window = CreateWindowEx(0, wc.ClassName, "F6 Launcher background", 0, 0, 0, 0, 0, 0, 0, wc.Instance, 0);
        if (_window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        while (GetMessage(out var msg, 0, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        RemoveIcon();
        DestroyWindow(_window);
        _window = 0;
    }
    internal static void SetVisible(bool value) { if (_window != 0) PostMessage(_window, SetVisibility, value ? 1 : 0, 0); }
    internal static void Quit() { if (_window != 0) PostMessage(_window, QuitMessage, 0, 0); }
    private static nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == SetVisibility)
        {
            bool visible = wParam != 0 && !_fixture;
            if (visible && !_visible) AddIcon();
            if (!visible) RemoveIcon();
            return 0;
        }
        if (message == _taskbarCreated && _visible) { _visible = false; AddIcon(); return 0; }
        if (message == QuitMessage) { RemoveIcon(); PostQuitMessage(0); return 0; }
        if (message == Callback)
        {
            uint action = (uint)((long)lParam & 0xffff);
            if (action is 0x202 or 0x203 or 0x400) _open?.Invoke(); // left click / keyboard select
            else if (action is 0x205 or 0x7b)
            {
                var menu = CreatePopupMenu();
                AppendMenu(menu, 0, 1, "打开 F6 Launcher");
                AppendMenu(menu, 0, 2, "退出启动器");
                GetCursorPos(out var point);
                SetForegroundWindow(window);
                uint command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, window, 0);
                DestroyMenu(menu);
                if (command == 1) _open?.Invoke();
                if (command == 2) { _exit?.Invoke(); Quit(); }
            }
            return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }
    private static void AddIcon()
    {
        var icon = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = _window, Id = 1,
            Flags = 1 | 2 | 4, Message = Callback, Icon = ExtractIcon(GetModuleHandle(null), Environment.ProcessPath!, 0),
            Tip = "F6 Launcher · 轻量模式（点击恢复界面）", Info = string.Empty, InfoTitle = string.Empty };
        _visible = ShellNotifyIcon(0, ref icon);
        if (icon.Icon != 0) DestroyIcon(icon.Icon);
    }
    private static void RemoveIcon()
    {
        if (!_visible) return;
        var icon = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = _window, Id = 1,
            Tip = string.Empty, Info = string.Empty, InfoTitle = string.Empty };
        ShellNotifyIcon(2, ref icon); _visible = false;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public nint Proc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? Menu; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Message; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message msg, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message msg);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint command, ref NotifyData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint ExtractIcon(nint instance, string file, uint index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
