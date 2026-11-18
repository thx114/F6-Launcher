using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace HoYoShadeHub.Extensions.Games;

public sealed record InstalledGameDirectory(string Id, string Group, string? Path);
public sealed record InstalledGameSize(string Id, string Path, long Bytes);
public sealed record InstalledGameSizes(IReadOnlyList<InstalledGameSize> Games, long LogicalBytes, long ActualBytes);

/// <summary>Streaming space accounting. Call from a worker; never retain every FileInfo or follow junctions.</summary>
public static class InstalledGameSizeScanner
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong Volume, Low, High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileIdInfo info, uint size);

    public static InstalledGameSizes Measure(IReadOnlyList<InstalledGameDirectory> directories,
        CancellationToken cancellationToken = default)
    {
        List<InstalledGameSize> games = [];
        long logical = 0, actual = 0;
        foreach (var group in directories.GroupBy(x => x.Group))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installed = group.Where(x => Directory.Exists(x.Path)).ToArray();
            // File identity checks are only needed when multiple servers can share files.
            HashSet<FileIdInfo>? identities = installed.Length > 1 && OperatingSystem.IsWindows() ? [] : null;
            foreach (var directory in installed)
            {
                long bytes = 0;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var file in new DirectoryInfo(directory.Path!).EnumerateFiles("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        long length = file.Length;
                        bool duplicate = false;
                        if (identities is not null)
                        {
                            try
                            {
                                using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete);
                                duplicate = GetFileInformationByHandleEx(handle, 18, out var id, 24)
                                    && !identities.Add(id);
                            }
                            // Count its logical size even if a busy file denies identity access.
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                        bytes += length;
                        if (!duplicate) actual += length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                games.Add(new(directory.Id, directory.Path!, bytes));
                logical += bytes;
            }
        }
        return new(games, logical, actual);
    }
}
