using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace HoYoShadeHub.Helpers;

internal sealed record PortableAppProcessSnapshot(IReadOnlyCollection<string> ImagePaths, bool Complete = true);

internal sealed class PortableAppCleanupResult
{
    public string? SkippedReason { get; set; }
    public string? RollbackDirectory { get; set; }
    public List<string> KeptDirectories { get; } = [];
    public List<string> RemovedDirectories { get; } = [];
    public List<string> DeferredDirectories { get; } = [];
}

/// <summary>Retires portable application payloads only after the selected GUI has loaded.</summary>
internal static class PortableAppCleanupPolicy
{
    internal const string StateRelativePath = ".hysx/portable-app-retention.json";
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private sealed class RetentionState
    {
        public RetentionState() { }
        public int Format { get; set; } = 1;
        public string Current { get; set; } = string.Empty;
        public string? Rollback { get; set; }
        // Persist before deleting: interrupted or locked payloads are retried even if partly removed.
        public List<string> Pending { get; set; } = [];
    }

    internal static PortableAppCleanupResult Run(string root, string currentDirectory,
        Func<PortableAppProcessSnapshot> readProcesses, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        currentDirectory = Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = new PortableAppCleanupResult();
        string current = Path.GetFileName(currentDirectory);
        if (!IsAppName(current) || !Names.Equals(Path.GetDirectoryName(currentDirectory), root))
        {
            result.SkippedReason = "Not a portable application directory";
            return result;
        }

        using FileStream? lease = PortableUpdatePolicy.TryAcquireMaintenance(root);
        if (lease is null)
        {
            result.SkippedReason = "An update or cleanup is in progress";
            return result;
        }

        string pointer = PortableUpdatePolicy.GetArchiveTarget(root, "version.ini");
        byte[] originalPointer = File.ReadAllBytes(pointer);
        string selectedExe = PortableUpdatePolicy.ReadVersionTarget(root,
            System.Text.Encoding.UTF8.GetString(originalPointer).TrimStart('\uFEFF'));
        string selected = Path.GetDirectoryName(selectedExe)!;
        if (!Names.Equals(selected, currentDirectory) || !File.Exists(selectedExe) || !IsCompletePayload(root, current))
        {
            result.SkippedReason = "The running GUI is not the selected complete application";
            return result;
        }

        string statePath = PortableUpdatePolicy.GetArchiveTarget(root, StateRelativePath);
        RetentionState? previous = ReadState(statePath);
        var candidates = new Dictionary<string, DirectoryInfo>(Names);
        foreach (string directory in Directory.EnumerateDirectories(root, "app-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            if (!IsAppName(name)) continue;
            try
            {
                if (IsCompletePayload(root, name) || previous?.Pending.Contains(name, Names) == true)
                    candidates[name] = new DirectoryInfo(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result.DeferredDirectories.Add(name + ": " + ex.Message);
            }
        }

        string? rollback = null;
        if (previous is not null)
        {
            string? remembered = Names.Equals(previous.Current, current) ? previous.Rollback : previous.Current;
            if (remembered is not null && !Names.Equals(remembered, current) && candidates.ContainsKey(remembered)
                && IsCompletePayload(root, remembered)) rollback = remembered;
        }
        // One-time migration for existing portable installations. EXE timestamps may be shared by hardlinks.
        rollback ??= candidates.Values.Where(item => !Names.Equals(item.Name, current) && IsCompletePayload(root, item.Name))
            .OrderByDescending(item => item.CreationTimeUtc).ThenByDescending(item => item.LastWriteTimeUtc)
            .ThenByDescending(item => item.Name, Names).Select(item => item.Name).FirstOrDefault();
        result.RollbackDirectory = rollback;

        var kept = new HashSet<string>(Names) { current };
        if (rollback is not null) kept.Add(rollback);
        PortableAppProcessSnapshot processes = readProcesses();
        if (!processes.Complete)
        {
            result.SkippedReason = "A running launcher process could not be identified";
            return result;
        }
        ProtectRunning(root, processes, kept);
        var state = new RetentionState
        {
            Current = current,
            Rollback = rollback,
            Pending = candidates.Keys.Where(name => !kept.Contains(name)).OrderBy(name => name, Names).ToList(),
        };
        if (!PointerUnchanged(pointer, originalPointer))
        {
            result.SkippedReason = "The selected application changed";
            return result;
        }
        SaveState(statePath, state);

        string[] planned = state.Pending.ToArray();
        foreach (string name in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PointerUnchanged(pointer, originalPointer))
            {
                result.SkippedReason = "The selected application changed";
                break;
            }
            // Refresh immediately before each removal; old tray/RPC helpers can outlive their GUI.
            processes = readProcesses();
            if (!processes.Complete)
            {
                result.SkippedReason = "A running launcher process could not be identified";
                break;
            }
            if (!PointerUnchanged(pointer, originalPointer))
            {
                result.SkippedReason = "The selected application changed";
                break;
            }
            ProtectRunning(root, processes, kept);
            if (kept.Contains(name))
            {
                state.Pending.Remove(name);
                continue;
            }
            try
            {
                DeletePayload(root, name, cancellationToken);
                result.RemovedDirectories.Add(name);
                state.Pending.Remove(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result.DeferredDirectories.Add(name + ": " + ex.Message);
            }
        }

        if (!state.Pending.SequenceEqual(planned, Names)) SaveState(statePath, state);
        result.KeptDirectories.AddRange(kept.OrderBy(name => name, Names));
        return result;
    }

    private static bool PointerUnchanged(string pointer, byte[] expected)
        => File.ReadAllBytes(pointer).AsSpan().SequenceEqual(expected);

    private static bool IsAppName(string name)
        => name.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
            && !name.Contains('/') && !name.Contains('\\') && !name.EndsWith('.') && !name.EndsWith(' ')
            && Version.TryParse(name[4..].Split('-', 2)[0], out _);

    private static bool IsCompletePayload(string root, string name)
    {
        string directory = PortableUpdatePolicy.GetArchiveTarget(root, name);
        if (!Directory.Exists(directory)) return false;
        return (File.Exists(Path.Combine(directory, "F6Launcher.exe")) || File.Exists(Path.Combine(directory, "HoYoShadeHub.exe")))
            && new[] { "F6Launcher", "HoYoShadeHub" }.Any(assembly =>
                File.Exists(Path.Combine(directory, assembly + ".dll"))
                && File.Exists(Path.Combine(directory, assembly + ".runtimeconfig.json")));
    }

    private static RetentionState? ReadState(string path)
    {
        if (!File.Exists(path)) return null;
        RetentionState? state = JsonSerializer.Deserialize<RetentionState>(File.ReadAllBytes(path));
        if (state is null || state.Format != 1 || !IsAppName(state.Current)
            || (state.Rollback is not null && !IsAppName(state.Rollback))
            || state.Pending is null || state.Pending.Any(name => !IsAppName(name)))
            throw new InvalidDataException("Invalid portable application retention state.");
        return state;
    }

    private static void SaveState(string path, RetentionState state)
        => PortableUpdatePolicy.WriteVersionAtomically(path, JsonSerializer.SerializeToUtf8Bytes(state));

    private static void ProtectRunning(string root, PortableAppProcessSnapshot snapshot, HashSet<string> kept)
    {
        foreach (string image in snapshot.ImagePaths)
        {
            if (!Path.IsPathFullyQualified(image)) continue;
            string relative = Path.GetRelativePath(root, image);
            string name = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (IsAppName(name)) kept.Add(name);
        }
    }

    private static void DeletePayload(string root, string name, CancellationToken cancellationToken)
    {
        string directory = PortableUpdatePolicy.GetArchiveTarget(root, name);
        if (!Directory.Exists(directory)) return;
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        // Inspect the entire tree first. Never recurse through a junction or alter shared file attributes.
        while (pending.TryPop(out string? parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PortableUpdatePolicy.GetArchiveTarget(root, Path.GetRelativePath(root, parent));
            directories.Add(parent);
            foreach (string child in Directory.EnumerateFileSystemEntries(parent))
            {
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Application payload contains a reparse point: " + child);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                else files.Add(child);
            }
        }
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PortableUpdatePolicy.GetArchiveTarget(root, Path.GetRelativePath(root, file));
            File.Delete(file); // Only unlink this directory entry; shared hardlink content remains intact.
        }
        foreach (string child in directories.OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PortableUpdatePolicy.GetArchiveTarget(root, Path.GetRelativePath(root, child));
            Directory.Delete(child, recursive: false);
        }
    }
}
