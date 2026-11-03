using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HoYoShadeHub.Extensions.Games;

public sealed record ExactStopCondition(int Pid, long CreationFileTime)
{
    public static ExactStopCondition? Parse(string[] args)
    {
        string[] names = ["--expected-pid", "--expected-creation-filetime"];
        bool requested = args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase) || a.StartsWith(n + "=", StringComparison.OrdinalIgnoreCase)));
        if (!requested) return null;
        if (args.Any(a => names.Any(n => a.StartsWith(n + "=", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("精确停止参数使用 --expected-pid <值> --expected-creation-filetime <值>，不能使用等号形式。");
        string Value(string name)
        {
            var indices = Enumerable.Range(0, args.Length).Where(i => args[i].Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (indices.Length != 1 || indices[0] + 1 >= args.Length) throw new ArgumentException(name + " 必须提供一次，使用独立参数值。");
            return args[indices[0] + 1];
        }
        if (!int.TryParse(Value(names[0]), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            throw new ArgumentException("expected-pid 必须为正 Int32 十进制 PID。");
        if (!long.TryParse(Value(names[1]), NumberStyles.None, CultureInfo.InvariantCulture, out long time) || time <= 0)
            throw new ArgumentException("expected-creation-filetime 必须为正 Int64 十进制 FILETIME 字符串。");
        try { _ = DateTime.FromFileTimeUtc(time); }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("expected-creation-filetime 超出 FILETIME 日期范围。"); }
        return new(pid, time);
    }
}

/// <summary>一次绑定持有同一内核进程句柄；实现不得按名字或 PID 再次打开。</summary>
public interface IExactStopProcess : IDisposable
{
    int Pid { get; }
    long CreationFileTime { get; }
    string ImagePath { get; }
    bool HasExited { get; }
    void RequestClose();
    void Terminate();
}

public static class ExactProcessStop
{
    public static async Task<string> StopAsync(ExactStopCondition expected, string expectedImagePath,
        Func<int, IExactStopProcess>? bind = null, Func<Task>? delay = null)
    {
        if (string.IsNullOrWhiteSpace(expectedImagePath) || !Path.IsPathFullyQualified(expectedImagePath))
            throw new InvalidOperationException("无法核验该游戏的可执行文件路径，拒绝停止。");
        using var process = (bind ?? (pid => new NativeProcess(pid)))(expected.Pid);
        if (process.HasExited || process.Pid != expected.Pid || process.CreationFileTime != expected.CreationFileTime
            || !Path.GetFullPath(process.ImagePath).Equals(Path.GetFullPath(expectedImagePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("进程已退出或 PID、创建 FILETIME、游戏路径不匹配，拒绝停止。");
        if (process.HasExited) throw new InvalidOperationException("核验后进程已退出，拒绝停止。");
        process.RequestClose();
        for (int i = 0; i < 20 && !process.HasExited; i++) await (delay?.Invoke() ?? Task.Delay(250));
        if (process.HasExited) return $"游戏已退出（精确 PID {expected.Pid}）";
        process.Terminate();
        if (!process.HasExited)
        {
            for (int i = 0; i < 20 && !process.HasExited; i++) await (delay?.Invoke() ?? Task.Delay(250));
            if (!process.HasExited) throw new IOException("精确进程终止请求后尚未退出。");
        }
        return $"游戏已强制退出（精确 PID {expected.Pid}）";
    }

    private sealed class NativeProcess : IExactStopProcess
    {
        private readonly SafeProcessHandle handle;
        public int Pid { get; }
        public long CreationFileTime { get; }
        public string ImagePath { get; }
        public bool HasExited => WaitForSingleObject(handle, 0) switch
        {
            0 => true, 258 => false, _ => throw new Win32Exception(Marshal.GetLastWin32Error())
        };
        public NativeProcess(int pid)
        {
            Pid = pid;
            handle = OpenProcess(0x1000 | 0x100000 | 1, false, pid);
            try
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!GetProcessTimes(handle, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                CreationFileTime = created;
                var image = new StringBuilder(32768); uint size = (uint)image.Capacity;
                if (!QueryFullProcessImageNameW(handle, 0, image, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                ImagePath = image.ToString();
            }
            catch { handle.Dispose(); throw; }
        }
        public void RequestClose()
        {
            // 持有进程对象直到退出/强制停止完成。窗口查询不重新绑定进程。
            Exception? failure = null;
            bool enumerated = EnumWindows((window, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(window, out uint pid);
                    if (pid == Pid && !HasExited && !PostMessageW(window, 0x10, IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    return true;
                }
                catch (Exception ex) { failure = ex; return false; }
            }, IntPtr.Zero);
            if (failure is not null) throw failure;
            if (!enumerated) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Terminate()
        {
            if (HasExited) return;
            if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => handle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle handle, uint code);
    private delegate bool WindowCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
