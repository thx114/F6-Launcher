using System.Text;
using System.Text.Json;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Services;

public static partial class OptiScalerProfiles
{
    private const string AuthorityFile = ".hub-profile-authority.json";
    private const string AuthorityBaseline = ".hub-authority-baseline";
    private sealed record Authority(string BuildDirectory, string GameKey);
    // Deterministic I/O/interleaving seam for the sequential regression host; never set by product code.
    internal static readonly System.Threading.AsyncLocal<Action<string>?> AuthorityTestCheckpoint = new();

    public static string AuthoritativeBuildDirectory(string directory)
    {
        lock (Gate)
        {
            var binding = ReadAuthority(directory);
            return binding?.BuildDirectory ?? directory;
        }
    }

    private static Authority? ReadAuthority(string directory)
    {
        string path = Path.Combine(directory, AuthorityFile);
        if (!File.Exists(path)) return null;
        var binding = JsonSerializer.Deserialize<Authority>(File.ReadAllText(path))
            ?? throw new IOException("Invalid authoritative profile binding.");
        if (!Path.IsPathFullyQualified(binding.BuildDirectory)
            || binding.GameKey != Sanitize(binding.GameKey) || binding.GameKey is "" or "." or ".."
            || Path.GetFullPath(binding.BuildDirectory).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid authoritative profile location.");
        return binding;
    }

    /// <summary>Official build profile is persistent authority; isolated INIs are projections only.</summary>
    public static bool PrepareAuthoritativeProfile(string runtimeDirectory, string buildDirectory, string gameKey)
    {
        lock (Gate)
        {
            try
            {
                if (!Path.IsPathFullyQualified(buildDirectory) || !Path.IsPathFullyQualified(runtimeDirectory)
                    || string.IsNullOrWhiteSpace(gameKey) || gameKey != Sanitize(gameKey) || gameKey is "." or ".."
                    || Path.GetFullPath(buildDirectory).Equals(Path.GetFullPath(runtimeDirectory), StringComparison.OrdinalIgnoreCase))
                    return false;
                var existing = ReadAuthority(runtimeDirectory);
                if (existing is not null)
                {
                    if (!existing.BuildDirectory.Equals(buildDirectory, StringComparison.OrdinalIgnoreCase)
                        || existing.GameKey != gameKey || !CollectRuntimeChangesChecked(runtimeDirectory).Succeeded) return false;
                }
                // Initial migration never treats an untracked stale cache as persistent authority.
                // Archive it before replacing it with the user's official game profile.
                string profile = ProfilePath(buildDirectory, gameKey);
                if (!File.Exists(profile) && !Activate(buildDirectory, gameKey)) return false;
                string main = Path.Combine(runtimeDirectory, OptiScalerRuntime.ConfigFileName);
                var paths = new[] { main, ProfilePath(runtimeDirectory, gameKey),
                    Path.Combine(runtimeDirectory, RuntimeBaselineFileName),
                    Path.Combine(runtimeDirectory, RuntimeOwnerFileName),
                    Path.Combine(runtimeDirectory, RuntimeTransactionFileName),
                    Path.Combine(runtimeDirectory, AuthorityFile), Path.Combine(runtimeDirectory, AuthorityBaseline), profile };
                AuthorityTestCheckpoint.Value?.Invoke("before-projection-snapshot");
                var snapshot = paths.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null,
                    StringComparer.OrdinalIgnoreCase);
                byte[] content = snapshot[profile] ?? throw new IOException("Official profile disappeared during projection.");
                if (snapshot[Path.Combine(runtimeDirectory, RuntimeTransactionFileName)] is not null) return false;
                ArchiveAuthoritySnapshot(runtimeDirectory, snapshot, gameKey);
                if (!SnapshotMatches(snapshot)) return false;
                return CommitAuthority(runtimeDirectory, new(buildDirectory, gameKey), content, snapshot, writeOfficial: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
            { return false; }
        }
    }

    private static RuntimeCollectionResult CollectAuthoritative(string directory, Authority binding)
    {
        string owner = binding.GameKey;
        if (RuntimeOwner(directory) != owner) return new(CollectionStatus.Blocked, owner);
        string profile = ProfilePath(binding.BuildDirectory, owner);
        string main = Path.Combine(directory, OptiScalerRuntime.ConfigFileName);
        string baseline = Path.Combine(directory, AuthorityBaseline);
        if (!File.Exists(profile) || !File.Exists(main) || !File.Exists(baseline))
            return new(CollectionStatus.Blocked, owner);
        var paths = new[] { profile, main, baseline, ProfilePath(directory, owner),
            Path.Combine(directory, RuntimeBaselineFileName), Path.Combine(directory, RuntimeOwnerFileName),
            Path.Combine(directory, AuthorityFile), Path.Combine(directory, RuntimeTransactionFileName) };
        var snapshot = paths.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null,
            StringComparer.OrdinalIgnoreCase);
        byte[] official = snapshot[profile]!, original = snapshot[baseline]!, live = snapshot[main]!;
        // Machine/runtime route keys are never copied into the persistent user profile.
        string runtime = OptiScalerRuntime.RestoreMachinePinnedKeys(DecodeIni(live), DecodeIni(original));
        // If the official baseline omitted the pinned key, absence must stay absence too.
        if (OptiScalerRuntime.ReadIniValue(DecodeIni(original), "Libraries", "OptiDllPath") is null)
        {
            var cleaned = IniDocument.LoadFromText(runtime);
            cleaned.RemoveKey("Libraries", "OptiDllPath");
            runtime = string.Join("\r\n", cleaned.Lines);
        }
        byte[] merged = MergeRuntimeIntoAuthority(official, original, Encoding.UTF8.GetBytes(runtime));
        if (merged.AsSpan().SequenceEqual(official) && official.AsSpan().SequenceEqual(original)
            && live.AsSpan().SequenceEqual(snapshot[Path.Combine(directory, RuntimeBaselineFileName)] ?? []))
            return new(CollectionStatus.Unchanged, owner);
        ArchiveAuthoritySnapshot(directory, snapshot, owner);
        if (!SnapshotMatches(snapshot)) return new(CollectionStatus.Blocked, owner);
        // Keep runtime-only paths/routes in the projection, with current official edits overlaid.
        string projected = OptiScalerRuntime.RestoreMachinePinnedKeys(DecodeIni(merged), DecodeIni(live));
        byte[] projection = Encoding.UTF8.GetBytes(projected);
        if (!CommitAuthority(directory, binding, merged, snapshot, writeOfficial: true, projection: projection))
            return new(CollectionStatus.Failed, owner);
        return new(CollectionStatus.Collected, owner);
    }

    private static byte[] MergeRuntimeIntoAuthority(byte[] official, byte[] baseline, byte[] runtime)
    {
        var result = IniDocument.LoadFromText(DecodeIni(official));
        var before = IniDocument.LoadFromText(DecodeIni(baseline));
        var live = IniDocument.LoadFromText(DecodeIni(runtime));
        bool changed = false;
        foreach (string section in live.GetSections().Concat(before.GetSections()).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (string key in live.GetSectionKeys(section).Concat(before.GetSectionKeys(section))
            .Where(k => !k.StartsWith(';') && !k.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? initial = before.GetValue(section, key), current = live.GetValue(section, key),
                saved = result.GetValue(section, key);
            bool Equal(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);
            // A newer official edit wins a same-key collision. Independent runtime edits survive.
            if (Equal(current, initial) || !Equal(saved, initial)) continue;
            if (current is null) result.RemoveKey(section, key);
            else result.SetValue(section, key, current);
            changed = true;
        }
        if (!changed) return official;
        var encoding = new UTF8Encoding(official.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(string.Join("\r\n", result.Lines).TrimEnd('\r', '\n') + "\r\n")];
    }

    private static void ArchiveAuthoritySnapshot(string directory, Dictionary<string, byte[]?> snapshot, string game)
    {
        // Official files live outside runtimeDirectory; do not use relative ".." backup paths.
        var local = snapshot.ToDictionary(pair => Path.Combine(directory,
            pair.Key.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(directory, pair.Key) : "authority-original.ini"), pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        ArchiveLaunchSnapshot(directory, local, game, "official-profile-authority");
    }

    private static bool CommitAuthority(string directory, Authority binding, byte[] official,
        Dictionary<string, byte[]?> snapshot, bool writeOfficial, byte[]? projection = null)
    {
        string transaction = Path.Combine(directory, RuntimeTransactionFileName);
        var written = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        bool Matches(string path, byte[]? bytes) => bytes is null ? !File.Exists(path)
            : File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        void Write(string path, byte[] bytes)
        {
            // Recheck immediately before touching each file, not just before the transaction.
            if (!snapshot.TryGetValue(path, out var original) || !Matches(path, original))
                throw new IOException("Configuration changed before transaction write: " + path);
            AtomicWrite(path, bytes);
            written.Add(path, bytes);
            AuthorityTestCheckpoint.Value?.Invoke("after-write:" + path);
        }
        try
        {
            Write(transaction, Encoding.UTF8.GetBytes(binding.GameKey));
            if (writeOfficial) Write(ProfilePath(binding.BuildDirectory, binding.GameKey), official);
            byte[] active = projection ?? official;
            Write(ProfilePath(directory, binding.GameKey), active);
            Write(Path.Combine(directory, OptiScalerRuntime.ConfigFileName), active);
            Write(Path.Combine(directory, RuntimeBaselineFileName), active);
            Write(Path.Combine(directory, RuntimeOwnerFileName), Encoding.UTF8.GetBytes(binding.GameKey));
            Write(Path.Combine(directory, AuthorityBaseline), official);
            Write(Path.Combine(directory, AuthorityFile), JsonSerializer.SerializeToUtf8Bytes(binding));
            foreach (var pair in snapshot)
                if (!Matches(pair.Key, written.TryGetValue(pair.Key, out var ours) ? ours : pair.Value))
                    throw new IOException("Configuration changed during transaction: " + pair.Key);
            File.Delete(transaction);
            return true;
        }
        catch
        {
            bool conflict = false;
            foreach (var pair in written.Reverse())
            {
                if (pair.Key.Equals(transaction, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // Never roll back a file we did not write, or clobber an external edit
                    // made after our write. The snapshot remains archived for recovery.
                    if (!Matches(pair.Key, pair.Value)) { conflict = true; continue; }
                    if (snapshot[pair.Key] is { } bytes) AtomicWrite(pair.Key, bytes);
                    else File.Delete(pair.Key);
                }
                catch { conflict = true; }
            }
            foreach (var pair in snapshot)
            {
                if (pair.Key.Equals(transaction, StringComparison.OrdinalIgnoreCase)) continue;
                try { if (!Matches(pair.Key, pair.Value)) conflict = true; }
                catch { conflict = true; }
            }
            // Retain our marker on conflict. Do not overwrite a foreign transaction marker.
            if (written.TryGetValue(transaction, out var marker))
            {
                if (!conflict)
                    try { if (Matches(transaction, marker)) File.Delete(transaction); } catch { }
                else
                    try
                    {
                        if (!File.Exists(transaction))
                        {
                            using var blocked = new FileStream(transaction, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            blocked.Write(marker);
                            blocked.Flush(flushToDisk: true);
                        }
                    }
                    catch { }
            }
            return false;
        }
    }
}
