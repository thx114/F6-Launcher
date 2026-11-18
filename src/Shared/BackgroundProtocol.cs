using System;
using System.Text.Json.Serialization;

namespace F6Launcher.Background;

public sealed record BackgroundRequest
{
    public string Command { get; init; } = string.Empty;
    public int OwnerPid { get; init; }
    public long OwnerStartTicks { get; init; }
    public string? GuiPath { get; init; }
    public string? UserData { get; init; }
    public GameWatch? Game { get; init; }
    public FpsAttach? Fps { get; init; }
}
public sealed record GameWatch(int Pid, long StartTicks, string GameKey, string? SessionDirectory,
    long StartedUtcTicks, SnapshotFile[] Files, string[] DumpDirectories, string[] OptiDirectories,
    string? DatabasePath, ProfileWriteback[] Profiles, bool RecordPlayTime = false);
public sealed record SnapshotFile(string Name, string Path, bool IsLog, bool Multiple);
public sealed record ProfileWriteback(string RuntimeDirectory, string GameKey);
public sealed record FpsAttach(int Pid, long StartTicks, int Target, byte[] Shellcode, byte[] Pattern, bool[] Mask, int TimeoutSeconds);
public sealed record BackgroundReply(bool Success, string? Error = null, int Pid = 0,
    long PrivateBytes = 0, long WorkingSetBytes = 0, bool SkippedGameExited = false, bool PatternNotFound = false,
    GameWatch? Game = null, GameWatch[]? Games = null);

[JsonSerializable(typeof(BackgroundRequest))]
[JsonSerializable(typeof(BackgroundReply))]
[JsonSerializable(typeof(GameWatch))]
[JsonSerializable(typeof(GameWatch[]))]
public partial class BackgroundJson : JsonSerializerContext;
