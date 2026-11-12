// 把 FPS 退休所需的 native 操作集中到可注入接口；测试不需要游戏或 WinUI。
using System;
using System.Runtime.InteropServices;

namespace HoYoShadeHub.Features.GameLauncher;

internal interface IFpsRemoteNative
{
    int LastError { get; }
    IntPtr Open(int pid);
    bool IsSameProcess(IntPtr originalProcess, IntPtr openedProcess);
    IntPtr Allocate(IntPtr process);
    bool Free(IntPtr process, IntPtr remote);
    bool Read(IntPtr process, IntPtr address, byte[] buffer, out nuint read);
    bool Write(IntPtr process, IntPtr address, byte[] buffer, out nuint written);
    bool Flush(IntPtr process, IntPtr address, nuint size);
    IntPtr StartThread(IntPtr process, IntPtr entry);
    uint Wait(IntPtr handle);
    bool Close(IntPtr handle);
    IntPtr AllocateSlot();
    void WriteSlot(IntPtr slot, int value);
    int ReadSlot(IntPtr slot);
    void FreeSlot(IntPtr slot);
}

internal sealed class FpsRemoteNative : IFpsRemoteNative
{
    public int LastError => Marshal.GetLastWin32Error();
    // SYNCHRONIZE | QUERY_INFORMATION | VM_WRITE | VM_READ | VM_OPERATION | CREATE_THREAD。
    public IntPtr Open(int pid) => OpenProcess(0x00100000 | 0x0400 | 0x0020 | 0x0010 | 0x0008 | 0x0002, false, pid);
    public bool IsSameProcess(IntPtr originalProcess, IntPtr openedProcess) => CompareObjectHandles(originalProcess, openedProcess);
    public IntPtr Allocate(IntPtr process) => VirtualAllocEx(process, IntPtr.Zero, 0x1000, 0x3000, 0x40);
    public bool Free(IntPtr process, IntPtr remote) => VirtualFreeEx(process, remote, 0, 0x8000);
    public bool Read(IntPtr process, IntPtr address, byte[] buffer, out nuint read)
        => ReadProcessMemory(process, address, buffer, (nuint)buffer.Length, out read);
    public bool Write(IntPtr process, IntPtr address, byte[] buffer, out nuint written)
        => WriteProcessMemory(process, address, buffer, (nuint)buffer.Length, out written);
    public bool Flush(IntPtr process, IntPtr address, nuint size) => FlushInstructionCache(process, address, size);
    public IntPtr StartThread(IntPtr process, IntPtr entry)
        => CreateRemoteThread(process, IntPtr.Zero, 0, entry, IntPtr.Zero, 0, out _);
    public uint Wait(IntPtr handle) => WaitForSingleObject(handle, 0);
    public bool Close(IntPtr handle) => CloseHandle(handle);
    public IntPtr AllocateSlot() => Marshal.AllocHGlobal(4);
    public void WriteSlot(IntPtr slot, int value) => Marshal.WriteInt32(slot, value);
    public int ReadSlot(IntPtr slot) => Marshal.ReadInt32(slot);
    public void FreeSlot(IntPtr slot) => Marshal.FreeHGlobal(slot);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("KernelBase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(IntPtr first, IntPtr second);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stackSize,
        IntPtr entry, IntPtr parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
