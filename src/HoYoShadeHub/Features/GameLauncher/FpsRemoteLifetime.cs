// FpsUnlocker 的资源所有者。已启动的 payload 可能有同步线程以外的执行者，
// 因而远端代码和本地回读槽位保留到原始 process handle 确认退出。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

internal sealed class FpsRemoteLifetime
{
    internal const uint Signaled = 0;
    internal const uint Timeout = 258;
    internal const uint WaitFailed = 0xFFFFFFFF;
    private static readonly object RetirementGate = new();
    private static readonly Dictionary<FpsRemoteLifetime, TaskCompletionSource> Retirements = new();

    internal readonly object Gate = new();
    internal readonly CancellationTokenSource Cancellation = new();
    private readonly IFpsRemoteNative _native;
    private IntPtr _slot;
    private IntPtr _process;
    private IntPtr _remote;
    private IntPtr _thread;
    private bool _started;
    private bool _attachCompleted;
    private bool _retired;
    private bool _complete;
    private Task? _loop;

    internal FpsRemoteLifetime(IFpsRemoteNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        try
        {
            _slot = native.AllocateSlot();
            if (_slot == IntPtr.Zero) throw new OutOfMemoryException("FPS slot 分配失败。");
            native.WriteSlot(_slot, 60);
        }
        catch
        {
            // 构造失败也已有真实 slot owner。交给同一退休路径，释放异常时仍可重试。
            CompleteAttach(null);
            Retire();
            throw;
        }
    }

    internal IntPtr Process => _process;
    internal IntPtr Remote => _remote;
    internal IntPtr Slot => _slot;
    internal string RetirementStatus { get; private set; } = "active";

    internal static bool HasPendingRetirements
    {
        get { lock (RetirementGate) return Retirements.Count != 0; }
    }

    internal void SetTarget(int fps)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            _native.WriteSlot(_slot, fps);
        }
    }

    internal int ReadTarget() => _native.ReadSlot(_slot);

    // Attach 是唯一生产者；退休必须等 CompleteAttach，不能抢走尚在构造的资源。
    internal bool Open(int pid, IntPtr originalProcess)
    {
        _process = _native.Open(pid);
        if (_process == IntPtr.Zero)
            throw new Win32Exception(_native.LastError, "OpenProcess 失败（请检查提权权限）。");
        // PID 可复用；调用方跨 await 固定原始 SafeProcessHandle，确认同一 kernel object
        // 后才能读目标内存。校验失败仍保留新开的 handle，交给退休路径关闭。
        return _native.IsSameProcess(originalProcess, _process);
    }

    internal void Install(byte[] shellcode, int entryOffset)
    {
        if (shellcode.Length > 0x1000 || entryOffset < 0 || entryOffset >= shellcode.Length)
            throw new ArgumentException("Payload 超出分配范围或入口无效。", nameof(shellcode));
        _remote = _native.Allocate(_process);
        if (_remote == IntPtr.Zero)
            throw new Win32Exception(_native.LastError, "VirtualAllocEx 失败。");
        if (!_native.Write(_process, _remote, shellcode, out nuint written) || written != (nuint)shellcode.Length)
            throw new Win32Exception(_native.LastError, "写 shellcode 失败或发生 partial write。");
        if (!_native.Flush(_process, _remote, (nuint)shellcode.Length))
            throw new Win32Exception(_native.LastError, "FlushInstructionCache 失败。");
        _thread = _native.StartThread(_process, _remote + entryOffset);
        if (_thread == IntPtr.Zero)
            throw new Win32Exception(_native.LastError, "创建远程线程失败。");
        _started = true;
    }

    internal bool Read(IntPtr address, byte[] buffer)
        => _native.Read(_process, address, buffer, out nuint read) && read == (nuint)buffer.Length;

    internal bool Write(IntPtr address, byte[] buffer)
        => _native.Write(_process, address, buffer, out nuint written) && written == (nuint)buffer.Length;

    internal uint WaitProcess() => _native.Wait(_process);

    internal void CompleteAttach(Task? loop)
    {
        lock (Gate)
        {
            _loop = loop;
            _attachCompleted = true;
        }
    }

    internal void Retire()
    {
        lock (Gate)
        {
            if (_retired) return;
            _retired = true;
            // 在可失败的取消操作前登记真实 owner；后台任务异常也不会丢失资源。
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (RetirementGate) Retirements.Add(this, completion);
            try { Cancellation.Cancel(); }
            finally { _ = Task.Run(() => PumpAsync(completion)); }
        }
    }

    /// <summary>无超时地等待当前退休队列清空；CLI 在正常退出前调用。</summary>
    internal static async Task DrainRetirementsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (RetirementGate) pending = Retirements.Values.Select(c => c.Task).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(TaskCompletionSource completion)
    {
        while (true)
        {
            try
            {
                if (TryRetire())
                {
                    lock (RetirementGate)
                    {
                        Retirements.Remove(this);
                        completion.SetResult();
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                RetirementStatus = $"retry: {ex.GetType().Name}";
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    // 可单步故障注入。所有 native wait 均为零超时，只有本 owner 能关闭被等待的 handle。
    internal bool TryRetire()
    {
        lock (Gate)
        {
            if (_complete) return true;
            if (!_retired || !_attachCompleted || (_loop != null && !_loop.IsCompleted))
            {
                RetirementStatus = "local users pending";
                return false;
            }

            bool exited = _process == IntPtr.Zero;
            if (!exited)
            {
                uint processWait = _native.Wait(_process);
                exited = processWait == Signaled;
                if (!exited && processWait != Timeout)
                {
                    RetirementStatus = "process wait failed; retaining owner";
                    return false;
                }
            }

            if (_started && !exited)
            {
                uint threadWait = _native.Wait(_thread);
                RetirementStatus = threadWait == Signaled ? "thread exited; retaining payload until process exit"
                    : threadWait == Timeout ? "thread pending; retaining owner" : "thread wait failed; retaining owner";
                // BuildShellcode 没有可验证的全局 quiescence/卸钩协议。清 PID 只影响
                // 入口的一次读取，并不能停止已缓存 PID/slot 的同步线程，故不尝试 stop 写。
                return false;
            }

            if (_remote != IntPtr.Zero)
            {
                if (!exited && !_native.Free(_process, _remote))
                {
                    RetirementStatus = "VirtualFreeEx failed; retaining owner";
                    return false;
                }
                // process signaled 后地址空间已由 Windows 回收，不能向复用的 PID 做 free。
                _remote = IntPtr.Zero;
            }
            if (_thread != IntPtr.Zero)
            {
                if (!_native.Close(_thread))
                {
                    RetirementStatus = "thread CloseHandle failed; retaining owner";
                    return false;
                }
                _thread = IntPtr.Zero;
            }
            if (_slot != IntPtr.Zero)
            {
                _native.FreeSlot(_slot);
                _slot = IntPtr.Zero;
            }
            if (_process != IntPtr.Zero)
            {
                if (!_native.Close(_process))
                {
                    RetirementStatus = "process CloseHandle failed; retaining owner";
                    return false;
                }
                _process = IntPtr.Zero;
            }
            Cancellation.Dispose();
            _complete = true;
            RetirementStatus = "complete";
            return true;
        }
    }
}
