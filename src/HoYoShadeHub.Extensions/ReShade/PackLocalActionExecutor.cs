using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using NuGet.Versioning;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 无 WinUI、全局 AppConfig 或真实进程依赖的本地动作执行边界。
/// LauncherActionRunner 与离线宿主使用同一 handler；环境目录和版本必须显式传入。
/// </summary>
public static class PackLocalActionExecutor
{
    public sealed class Context
    {
        public GameEntry? Entry { get; init; }
        public ShadeHost? Host { get; init; }
        public GamePluginService? Plugins { get; init; }
        public string? PackRoot { get; init; }
        public string? LauncherDirectory { get; init; }
        public string LauncherVersion { get; init; } = string.Empty;
    }

    public static Task<string> ExecuteAsync(PackActionStep step, Context context)
        => step.Action.Trim().ToLowerInvariant() switch
        {
            "require_launcher_version" or "request_launcher_version" => Task.FromResult(CheckLauncherVersion(step, context.LauncherVersion)),
            "set_addon" or "set_addons" => Task.FromResult(SetAddons(step, context)),
            "import_preset" => Task.FromResult(ImportPreset(step, context)),
            "apply_preset" => Task.FromResult(ApplyPreset(step, context)),
            "clear_game_ini" => Task.FromResult(ClearGameIni(context)),
            "set_ini_keys" or "set_ini" or "write_ini" => Task.FromResult(SetIniKeys(step, context)),
            "override_files" => Task.FromResult(OverrideFiles(step, context)),
            "copy_file" or "place_file" => PlaceFileAsync(step, context),
            "import_shader" => Task.FromResult(ImportShader(step, context)),
            "set_shader" => Task.FromResult(SetShader(step, context)),
            "delete_shader" => Task.FromResult(DeleteShader(step, context)),
            "wait" or "sleep" or "delay" => WaitAsync(step),
            _ => throw new NotSupportedException("此动作不属于本地执行边界：" + step.Action),
        };

    public static string CheckLauncherVersion(PackActionStep step, string currentVersion)
    {
        string? minimum = step.GetString("minVersion") ?? step.GetString("version");
        if (string.IsNullOrWhiteSpace(minimum) || !NuGetVersion.TryParse(minimum.Trim(), out var required))
            return "✗ require_launcher_version：需要有效的 minVersion（例如 1.4.7.1）。";
        if (!NuGetVersion.TryParse(currentVersion, out var current))
            return $"✗ 无法识别当前启动器版本 {currentVersion}，请更新启动器后重新导入覆盖包。";
        return current.CompareTo(required) >= 0
            ? $"启动器版本符合要求：当前 {currentVersion}，要求 ≥ {minimum.Trim()}"
            : $"✗ 覆盖包要求启动器 ≥ {minimum.Trim()}，当前为 {currentVersion}。请先更新启动器，再重新导入覆盖包。";
    }

    /// <summary>wait / sleep / delay：按毫秒或秒暂停后续动作，供 CLI 自动测试使用。</summary>
    public static async Task<string> WaitAsync(PackActionStep step, double? defaultMilliseconds = null)
    {
        double? milliseconds = step.GetNumber("milliseconds")
            ?? step.GetNumber("ms")
            ?? step.GetNumber("duration_ms");
        if (milliseconds is null && step.GetNumber("seconds") is { } seconds)
        {
            milliseconds = seconds * 1000.0;
        }

        milliseconds ??= defaultMilliseconds;
        if (milliseconds is null)
        {
            return "✗ wait 需要 milliseconds/ms/duration_ms 或 seconds 参数";
        }

        if (!double.IsFinite(milliseconds.Value) || milliseconds.Value < 0 || milliseconds.Value > 3_600_000)
        {
            return "✗ wait 时间必须在 0 到 3600000 毫秒之间";
        }

        int delay = (int)Math.Round(milliseconds.Value, MidpointRounding.AwayFromZero);
        await Task.Delay(delay);
        return $"已等待 {delay} ms";
    }

    // ==================== 插件 ====================

    /// <summary>set_addon(s)：开关插件。files 缺省 / 空 = 全部插件（「关闭全部插件」就是这个）。</summary>
    public static string SetAddons(PackActionStep step, Context context)
    {
        bool? enabled = step.GetBool("enabled");
        if (enabled is null)
        {
            return "✗ set_addons 缺 enabled 参数";
        }

        GamePluginService? plugins = context.Plugins;
        if (plugins is null)
        {
            return "✗ 这个游戏还没有 ReShade.ini，插件开关不可用";
        }

        List<string> files = step.GetStringList("files");
        List<GameAddonState> addons = plugins.GetAddons();
        List<GameAddonState> targets = files.Count == 0
            ? addons
            : addons.Where(a => files.Any(f =>
                string.Equals(a.FileName, f, StringComparison.OrdinalIgnoreCase))).ToList();

        if (targets.Count == 0)
        {
            return $"✗ 没有匹配到插件（files={string.Join(",", files)}）";
        }

        int ok = 0;
        foreach (GameAddonState addon in targets)
        {
            if (plugins.SetAddonEnabled(addon.FileName, enabled.Value))
            {
                ok++;
            }
        }

        if (ok != targets.Count)
            return $"✗ {(enabled.Value ? "启用" : "禁用")}插件未全部完成 {ok}/{targets.Count}；下次启动重试";

        return $"{(enabled.Value ? "启用" : "禁用")}插件 {ok}/{targets.Count}"
               + (files.Count == 0 ? "（全部）" : string.Empty);
    }

    // ==================== 预设 ====================

    public static string ImportPreset(PackActionStep step, Context context)
    {
        string? addonDir = context.Plugins?.AddonDirectory
                           ?? context.Host?.AddonsPath;
        if (string.IsNullOrWhiteSpace(addonDir))
        {
            return "✗ 没有插件目录，导不了预设";
        }

        string? shareCode = step.GetString("shareCode") ?? step.GetString("code");
        if (!string.IsNullOrWhiteSpace(shareCode))
        {
            string? target = Dlss5PresetLibrary.ImportShareCode(addonDir, shareCode, step.GetString("name"));
            return target is null ? "✗ 分享码解不开" : "已导入分享码 → " + Path.GetFileName(target);
        }

        // 包内文件（presets\ 下的相对路径）
        string? file = step.GetString("file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return "✗ import_preset 需要 shareCode 或 file 参数";
        }

        string? source = ResolvePackFile(context, file!);
        if (source is null)
        {
            return $"✗ 包里找不到 {file}";
        }

        string? imported = Dlss5PresetLibrary.ImportFile(addonDir, source);
        return imported is null ? "✗ 预设导入失败" : "已导入预设 → " + Path.GetFileName(imported);
    }

    /// <summary>apply_preset：把 DLSS5-Presets 里的一个预设设成这个游戏的当前 ReShade 预设</summary>
    public static string ApplyPreset(PackActionStep step, Context context)
    {
        string? name = step.GetString("name") ?? step.GetString("file");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ apply_preset 需要 name 参数";
        }

        if (context.Entry?.ReShadeIniPath is not { } ini || !File.Exists(ini))
        {
            return "✗ 这个游戏还没有 ReShade.ini";
        }

        string? addonDir = context.Plugins?.AddonDirectory
                           ?? ReShadeProfile.Load(ini).ResolveAddonDirectory();
        if (string.IsNullOrWhiteSpace(addonDir))
        {
            return "✗ 找不到插件目录";
        }

        // 先认 DLSS5-Presets（各级子目录），再认 Addons 顶层，最后认包内文件
        string presetFolder = Path.Combine(addonDir, Dlss5PresetLibrary.PresetFolderName);
        string? preset = Directory.Exists(presetFolder)
            ? Directory.EnumerateFiles(presetFolder, "*.ini", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
            : null;
        preset ??= Directory.EnumerateFiles(addonDir, "*.ini", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        preset ??= ResolvePackFile(context, name!);

        if (preset is null)
        {
            return $"✗ 找不到预设 {name}";
        }

        try
        {
            ReShadeProfile profile = ReShadeProfile.Load(ini);
            profile.SetValue("GENERAL", "PresetPath", preset);
            profile.Save();
            return "当前预设 → " + Path.GetFileName(preset);
        }
        catch (Exception ex)
        {
            return "✗ 写 PresetPath 失败：" + ex.Message;
        }
    }

    // ==================== ini ====================

    /// <summary>clear_game_ini：删掉游戏目录的 ReShade.ini / ReShade2.ini（下次启动按模板 + 覆盖包重建）</summary>
    public static string ClearGameIni(Context context)
    {
        if (context.Entry?.ReShadeIniPath is not { } ini)
        {
            return "✗ 不知道这个游戏目录在哪";
        }

        int deleted = 0, failed = 0;
        foreach (string file in new[]
        {
            ini,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ini))!, GameIniBootstrap.SecondaryIniFileName),
        })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch
            {
                failed++;
            }
        }

        return failed > 0 ? $"✗ 删除游戏 ini 未全部完成（已删除 {deleted}，失败 {failed}）；once 保留 pending。"
            : deleted > 0 ? $"已删除 {deleted} 份游戏 ini" : "游戏 ini 本来就不在";
    }

    /// <summary>
    /// set_ini_keys：把任意节/键写进 ini（默认游戏 ReShade.ini；file= 可指游戏目录内的其它 ini，
    /// 相对路径）。set = { "节": { "键": "值" } }，remove = { "节": ["键"] }。
    /// 包动作按逻辑步骤在 .hysx_once.json 记账；已成功步骤不重跑。
    /// 首次使用可生成目标 INI；历史 pending 仅在用户明确确认后重试此配置步骤。
    /// </summary>
    public static string SetIniKeys(PackActionStep step, Context context)
    {
        string target = step.GetString("file") ?? "ReShade.ini";
        try
        {
            target = PackIniAction.ResolveTarget(step, context.Entry);
            PackIniAction.Result result = PackIniAction.Apply(step, context.Entry, context.Host);
            return $"已写入 {Path.GetFileName(result.Path)}：set {result.SetKeys} 键 / remove {result.RemoveKeys} 键"
                + (result.Created ? "（首次生成）" : result.Changed ? string.Empty : "（值相同，无改动）");
        }
        catch (Exception ex)
        {
            return $"✗ set_ini_keys：无法写入 {target}：{ex.Message}";
        }
    }

    /// <summary>override_files：把包里的目录覆盖到目标。target = game（游戏目录）| launcher（启动器目录）。</summary>
    public static string OverrideFiles(PackActionStep step, Context context)
    {
        string target = (step.GetString("target") ?? "game").Trim().ToLowerInvariant();
        string folder = step.GetString("folder") ?? (target == "launcher"
            ? GameAddonPackUserContent.LauncherFilesFolderName
            : GameAddonPackUserContent.GameFilesFolderName);

        string? targetRoot = target switch
        {
            "game" => context.Entry?.GameDirectory,
            "launcher" => context.LauncherDirectory,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            return $"✗ 不知道{(target == "launcher" ? "启动器" : "游戏")}目录在哪";
        }

        string source = context.PackRoot is null ? string.Empty : Path.Combine(context.PackRoot, folder);
        int expected = Directory.Exists(source)
            ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count() : 0;
        int copied = GameAddonPackUserContent.OverlayFiles(context.PackRoot, folder, targetRoot);
        if (copied != expected)
            return $"✗ 覆盖文件可能部分完成（{copied}/{expected}）；once 保留 pending，禁止自动重试。";
        return copied > 0 ? $"已覆盖 {copied} 个文件到{target switch { "game" => "游戏", "launcher" => "启动器", _ => target }}目录"
            : "✗ 没有可覆盖的文件（包里没有 " + folder + "\\）";
    }

    // ==================== ReShade 滤镜 ====================

    private static string HostShadersDirectory(Context context)
        => context.Host is { } host ? Path.Combine(host.RootPath, "reshade-shaders") : string.Empty;

    /// <summary>import_shader：把包里的 .fx 复制进宿主 reshade-shaders 目录（EffectSearchPaths 指的那里）</summary>
    public static string ImportShader(PackActionStep step, Context context)
    {
        string? file = step.GetString("file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return "✗ import_shader 需要 file 参数";
        }

        string? source = ResolvePackFile(context, file!);
        string shaders = HostShadersDirectory(context);
        if (source is null || shaders.Length == 0)
        {
            return source is null ? $"✗ 包里找不到 {file}" : "✗ 没有 HoYoShade 宿主";
        }

        try
        {
            string target = Path.Combine(shaders, Path.GetFileName(source));
            Directory.CreateDirectory(shaders);
            File.Copy(source, target, overwrite: true);
            return "已导入滤镜：" + Path.GetFileName(target);
        }
        catch (Exception ex)
        {
            return "✗ 滤镜导入失败：" + ex.Message;
        }
    }

    /// <summary>set_shader：开关当前 ReShade 预设里的 technique（"Name@file.fx" 或裸 technique 名）</summary>
    public static string SetShader(PackActionStep step, Context context)
    {
        string? name = step.GetString("name") ?? step.GetString("technique");
        if (string.IsNullOrWhiteSpace(name) || step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_shader 需要 name 和 enabled 参数";
        }

        if (context.Entry?.ReShadeIniPath is not { } ini || !File.Exists(ini))
        {
            return "✗ 这个游戏还没有 ReShade.ini";
        }

        string? presetPath = ReShadeProfile.Load(ini).ResolvePresetPath();
        if (presetPath is null || !File.Exists(presetPath))
        {
            return "✗ 当前预设不存在（先在游戏里 / 预设管理里选好预设）";
        }

        try
        {
            IniDocument preset = IniDocument.Load(presetPath);
            bool changed = enabled ? EnableTechnique(preset, name!) : DisableTechnique(preset, name!);
            if (changed)
            {
                preset.Save(presetPath);
            }

            return $"{(changed ? "已" : "本来就在目标状态，")}{(enabled ? "开启" : "关闭")} technique {name}";
        }
        catch (Exception ex)
        {
            return "✗ 写预设失败：" + ex.Message;
        }
    }

    /// <summary>delete_shader：删掉宿主 reshade-shaders 里的 .fx，并从当前预设摘掉它的 technique</summary>
    public static string DeleteShader(PackActionStep step, Context context)
    {
        string? name = step.GetString("name") ?? step.GetString("file");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ delete_shader 需要 name 参数（.fx 文件名）";
        }

        string fxName = name!.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) ? name! : name! + ".fx";
        int deleted = 0, failed = 0;
        string shaders = HostShadersDirectory(context);
        if (shaders.Length > 0 && Directory.Exists(shaders))
        {
            foreach (string file in Directory.EnumerateFiles(shaders, fxName, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    failed++;
                }
            }
        }

        // 预设里的 technique 一并摘掉（按 @文件名 认）
        string presetNote = string.Empty;
        if (context.Entry?.ReShadeIniPath is { } ini && File.Exists(ini))
        {
            string? presetPath = ReShadeProfile.Load(ini).ResolvePresetPath();
            if (presetPath is not null && File.Exists(presetPath))
            {
                try
                {
                    IniDocument preset = IniDocument.Load(presetPath);
                    List<string> all = SplitList(preset.GetValue(IniDocument.RootSection, "Techniques"));
                    List<string> kept = [.. all.Where(e => !MatchesEffectFile(e, fxName))];
                    if (kept.Count != all.Count)
                    {
                        preset.SetValue(IniDocument.RootSection, "Techniques", string.Join(',', kept));
                        preset.Save(presetPath);
                        presetNote = "，预设里的 technique 已摘";
                    }
                }
                catch
                {
                    failed++;
                }
            }
        }

        return failed > 0 ? $"✗ 删除滤镜可能部分完成（已删除 {deleted}，失败 {failed}）；禁止自动重试。"
            : deleted > 0 ? $"已删除滤镜 {fxName}{presetNote}" : $"✗ reshade-shaders 里没有 {fxName}";
    }

    private static bool EnableTechnique(IniDocument preset, string technique)
    {
        bool changed = false;
        List<string> techniques = SplitList(preset.GetValue(IniDocument.RootSection, "Techniques"));
        if (!techniques.Any(e => EntryMatches(e, technique)))
        {
            techniques.Add(technique);
            preset.SetValue(IniDocument.RootSection, "Techniques", string.Join(',', techniques));
            changed = true;
        }

        List<string> sorting = SplitList(preset.GetValue(IniDocument.RootSection, "TechniqueSorting"));
        if (!sorting.Any(e => EntryMatches(e, technique)))
        {
            sorting.Add(technique);
            preset.SetValue(IniDocument.RootSection, "TechniqueSorting", string.Join(',', sorting));
            changed = true;
        }

        return changed;
    }

    private static bool DisableTechnique(IniDocument preset, string technique)
    {
        string? raw = preset.GetValue(IniDocument.RootSection, "Techniques");
        if (raw is null)
        {
            return false;
        }

        List<string> all = SplitList(raw);
        List<string> kept = [.. all.Where(e => !EntryMatches(e, technique))];
        if (kept.Count == all.Count)
        {
            return false;
        }

        preset.SetValue(IniDocument.RootSection, "Techniques", string.Join(',', kept));
        return true;
    }

    /// <summary>technique 条目匹配：全等、或用户给的名字是条目 @ 前缀部分 / 条目 @ 后面的 .fx 文件名</summary>
    private static bool EntryMatches(string entry, string name)
    {
        string trimmed = entry.Trim();
        return string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith(name + "@", StringComparison.OrdinalIgnoreCase)
               || MatchesEffectFile(trimmed, name);
    }

    private static bool MatchesEffectFile(string entry, string fxName)
    {
        int at = entry.LastIndexOf('@');
        return at >= 0 && string.Equals(entry[(at + 1)..].Trim(), fxName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitList(string? raw) => string.IsNullOrWhiteSpace(raw)
        ? []
        : [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>包内文件解析：相对路径按包根找，绝对路径原样校验存在性</summary>
    public static string? ResolvePackFile(Context context, string relativeOrAbsolute)
    {
        try
        {
            if (Path.IsPathFullyQualified(relativeOrAbsolute))
            {
                return File.Exists(relativeOrAbsolute) ? relativeOrAbsolute : null;
            }

            if (context.PackRoot is null)
            {
                return null;
            }

            string candidate = Path.GetFullPath(Path.Combine(context.PackRoot, relativeOrAbsolute));
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
    public static async Task<string> PlaceFileAsync(PackActionStep step, Context context)
    {
        var result = await Task.Run(() =>
        {
            var plan = PackFilePlacement.Resolve(step, context.PackRoot, context.Entry?.GameDirectory,
                context.LauncherDirectory, context.Host?.RootPath);
            return PackFilePlacement.Apply(plan, context.PackRoot!);
        });
        return (result.Changed ? "已放置文件：" : "文件内容一致，已保留：") + result.Destination
            + (result.Backup is null ? string.Empty : "；原文件备份：" + result.Backup);
    }
}
