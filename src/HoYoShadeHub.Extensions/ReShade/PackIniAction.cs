using System.Text.Json;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>执行已获同意的声明式 INI 写入；默认目标可在首次安装时生成。</summary>
public static class PackIniAction
{
    public sealed record Result(string Path, bool Changed, bool Created, int SetKeys, int RemoveKeys);

    public static bool IsIniAction(PackActionStep step) =>
        step.Action.Trim().ToLowerInvariant() is "set_ini_keys" or "set_ini" or "write_ini";

    public static bool UsesReShadeIni(PackActionStep step) =>
        string.IsNullOrWhiteSpace(step.GetString("file"))
        || string.Equals(step.GetString("file")?.Trim().Replace('\\', '/'), "ReShade.ini", StringComparison.OrdinalIgnoreCase)
        || string.Equals(step.GetString("file")?.Trim().Replace('\\', '/'), "./ReShade.ini", StringComparison.OrdinalIgnoreCase);

    public static string ResolveTarget(PackActionStep step, GameEntry? entry)
    {
        string? gameDirectory = entry?.GameDirectory;
        if (string.IsNullOrWhiteSpace(gameDirectory))
            throw new IOException("当前游戏目录未确定，无法定位目标 INI。");
        string file = step.GetString("file")?.Trim() is { Length: > 0 } specified ? specified : "ReShade.ini";
        string target = Path.GetFullPath(Path.Combine(gameDirectory, file));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(file) || !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"INI 目标必须是游戏目录内的相对路径：{file}");
        return target;
    }

    public static Result Apply(PackActionStep step, GameEntry? entry, ShadeHost? host)
    {
        string target = ResolveTarget(step, entry);
        var config = new GamePackIniConfig();
        int setKeys = 0, removeKeys = 0;
        if (step.Raw.ValueKind == JsonValueKind.Object
            && step.Raw.TryGetProperty("set", out JsonElement set) && set.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty section in set.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Object) continue;
                var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty key in section.Value.EnumerateObject())
                {
                    keys[key.Name] = key.Value.ValueKind == JsonValueKind.String
                        ? key.Value.GetString() ?? string.Empty : key.Value.ToString();
                    setKeys++;
                }
                if (keys.Count > 0) config.Set[section.Name] = keys;
            }
        }
        if (step.Raw.ValueKind == JsonValueKind.Object
            && step.Raw.TryGetProperty("remove", out JsonElement remove) && remove.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty section in remove.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Array) continue;
                List<string> keys = section.Value.EnumerateArray()
                    .Where(key => key.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString()))
                    .Select(key => key.GetString()!).ToList();
                if (keys.Count > 0) config.Remove[section.Name] = keys;
                removeKeys += keys.Count;
            }
        }
        if (config.IsEmpty) throw new InvalidDataException($"没有可写入或移除的 INI 键：{target}");
        bool created = !File.Exists(target);
        if (created && string.Equals(target, entry?.ReShadeIniPath, StringComparison.OrdinalIgnoreCase))
            GameLaunchIniPreparation.EnsureReShadeTarget(entry, host);
        if (File.Exists(target))
            return new(target, config.ApplyStrict(target), created, setKeys, removeKeys);
        // A named INI is created only when this exact action requests writing it. No special NR file is required.
        if (!string.Equals(Path.GetExtension(target), ".ini", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("目标配置文件不存在；仅为显式指定的 .ini 文件自动创建。", target);
        if (setKeys == 0) return new(target, false, false, setKeys, removeKeys);
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".f6-ini-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            config.ApplyStrict(temp);
            try { File.Move(temp, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            {
                return new(target, config.ApplyStrict(target), false, setKeys, removeKeys);
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(target, true, true, setKeys, removeKeys);
    }
}
