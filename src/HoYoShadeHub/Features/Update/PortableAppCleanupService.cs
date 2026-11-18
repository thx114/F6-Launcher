using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Update;

internal static class PortableAppCleanupService
{
    private static int _scheduled;

    internal static void ScheduleAfterStartup(CancellationToken cancellationToken)
    {
        if (!AppConfig.IsPortable || Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            var logger = AppConfig.GetLogger<PortableAppCleanupResult>();
            try
            {
                // MainView has loaded; leave startup rendering and initial disk reads time to settle.
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                string current = AppContext.BaseDirectory;
                string root = Directory.GetParent(Path.TrimEndingDirectorySeparator(current))!.FullName;
                PortableAppCleanupResult result = PortableAppCleanupPolicy.Run(root, current, ReadProcesses, cancellationToken);
                logger.LogInformation("Portable app cleanup: kept={Kept}; rollback={Rollback}; removed={Removed}; deferred={Deferred}; skipped={Skipped}",
                    string.Join(", ", result.KeptDirectories), result.RollbackDirectory,
                    string.Join(", ", result.RemovedDirectories), result.DeferredDirectories.Count, result.SkippedReason);
                foreach (string deferred in result.DeferredDirectories)
                    logger.LogInformation("Portable app cleanup deferred: {Reason}", deferred);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Portable app cleanup deferred until the next successful startup");
            }
        }, cancellationToken);
    }

    internal static PortableAppProcessSnapshot ReadProcesses()
    {
        var paths = new System.Collections.Generic.List<string>();
        bool complete = true;
        var image = new StringBuilder(32768);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { continue; }
                try
                {
                    using SafeProcessHandle handle = OpenProcess(0x1000, false, process.Id);
                    image.Clear();
                    uint size = (uint)image.Capacity;
                    if (!handle.IsInvalid && QueryFullProcessImageNameW(handle, 0, image, ref size))
                    {
                        paths.Add(image.ToString());
                        continue;
                    }
                    // Unrelated protected system processes do not own launcher payloads.
                    if (IsLauncherProcess(name) && !process.HasExited) complete = false;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // Exited during enumeration.
                catch (Win32Exception)
                {
                    if (IsLauncherProcess(name)) complete = false;
                }
            }
        }
        return new PortableAppProcessSnapshot(paths, complete);
    }

    private static bool IsLauncherProcess(string name)
        => name.StartsWith("F6Launcher", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("HoYoShadeHub", StringComparison.OrdinalIgnoreCase)
            || name.Equals("F6ShadeInjector", StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
}
