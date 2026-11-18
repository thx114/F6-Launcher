using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.OptiScaler;
using System;
using System.IO;
using System.Text;

namespace HoYoShadeHub.Features.Plugins;

// These exact handlers are also linked into the offline fixture host. Only AppConfig/environment differs.
public static partial class LauncherActionRunner
{
    /// <summary>import_opt_config：把包里的 ini 存成 OptiScaler 配置（OptiScalerPresets）</summary>
    private static string ImportOptConfig(PackActionStep step, LauncherActionContext context)
    {
        string? file = step.GetString("file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return "✗ import_opt_config 需要 file 参数";
        }

        string? source = ResolvePackFile(context, file!);
        if (source is null)
        {
            return $"✗ 包里找不到 {file}";
        }

        string name = step.GetString("name") ?? Path.GetFileNameWithoutExtension(file!);
        return OptiScalerPresets.ImportFile(source, name)
            ? "已导入 OptiScaler 配置：" + name
            : "✗ 配置导入失败（重名 / 读不了 / 写不进配置目录）";
    }

    /// <summary>update_opt_config：把当前游戏生效的那份 OptiScaler 配置回抓成预设（CaptureFromBuild）</summary>
    private static string UpdateOptConfig(PackActionStep step, LauncherActionContext context)
    {
        string? name = step.GetString("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ update_opt_config 需要 name 参数（存成哪个配置名）";
        }

        string? dll = AppConfig.GetSelectedOptiScalerDll(context.GameId);
        string? buildDir = dll is null ? null : Path.GetDirectoryName(Path.GetFullPath(dll));
        if (string.IsNullOrWhiteSpace(buildDir) || !Directory.Exists(buildDir))
        {
            return "✗ 这个游戏没选 OptiScaler 构建";
        }

        return OptiScalerPresets.CaptureFromBuild(buildDir, name!)
            ? "已把当前配置回抓为：" + name
            : "✗ 回抓失败（构建目录读不了）";
    }

    /// <summary>
    /// set_opt_config：把一份已导入的 OptiScaler 配置选成当前游戏生效的配置 ——
    /// 等于 OptiScaler 页下拉里手动选它：记选择（opti_preset_&lt;游戏&gt;_&lt;构建&gt; + follow 键）、
    /// 写 profiles\&lt;游戏&gt;.ini 并激活成主 ini。要在 set_opt_build 和 import_opt_config 之后跑。
    /// </summary>
    private static string SetOptConfig(PackActionStep step, LauncherActionContext context)
    {
        string? name = step.GetString("name") ?? step.GetString("preset");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ set_opt_config 需要 name 参数";
        }

        if (context.GameId is not { } gameId)
        {
            return "✗ set_opt_config：没有当前游戏";
        }

        string? buildId = AppConfig.GetSelectedOptiScalerId(gameId);
        if (string.IsNullOrWhiteSpace(buildId))
        {
            return "✗ 这个游戏还没选 OptiScaler 构建（先跑 set_opt_build）";
        }

        bool ifMissing = step.GetBool("if_missing") == true;
        // 初始化检查必须落在 per-game 所选的原始构建，不能误认隔离 runtime 的 profile。
        string? dll = ifMissing
            ? new OptiScalerLibrary(AppConfig.OptiScalerRootPath).List(includeDirectorySize: false)
                .Find(b => string.Equals(b.Id, buildId, StringComparison.OrdinalIgnoreCase))?.DllPath
            : AppConfig.GetSelectedOptiScalerDll(gameId);
        string? buildDir = dll is null ? null : Path.GetDirectoryName(Path.GetFullPath(dll));
        if (string.IsNullOrWhiteSpace(buildDir) || !Directory.Exists(buildDir))
        {
            return "✗ OptiScaler 构建目录不存在：" + buildId;
        }

        string gameKey = gameId.GameBiz.ToString();
        if (ifMissing)
        {
            if (File.Exists(OptiScalerProfiles.ProfilePath(buildDir, gameKey)))
                return "已跳过 OptiScaler 配置：游戏 profile 已存在（if_missing）";

            // 覆盖包不携带 root main/profile；仅首次安装缺少 main 时引导既有 Apply。
            string mainIni = Path.Combine(buildDir, OptiScalerRuntime.ConfigFileName);
            try
            {
                if (!File.Exists(mainIni))
                {
                    string? content = OptiScalerPresets.Read(name!);
                    if (content is null) return "✗ 初始化配置失败：找不到预设 " + name;
                    try
                    {
                        using var output = new FileStream(mainIni, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        using var writer = new StreamWriter(output, new UTF8Encoding(false));
                        writer.Write(content);
                    }
                    catch (IOException ex) when (((ex.HResult & 0xFFFF) is 80 or 183) && File.Exists(mainIni))
                    {
                        // 另一个初始化者已创建 main；不覆盖，交给 Apply 的既有激活流程。
                    }
                }
            }
            catch (Exception ex) { return "✗ 初始化 OptiScaler main 配置失败：" + ex.Message; }

            if (!OptiScalerPresets.Apply(buildDir, gameKey, name!))
                return $"✗ 套用配置失败（presets 目录里没有 {name}.ini？）";
            AppConfig.SetValue(name, $"opti_preset_{gameKey}_{buildId}");
            AppConfig.SetValue(name, OptiScalerPresets.FollowKey(gameKey));
            return $"OptiScaler 配置 → {name}（已首次写入 profiles 并激活）";
        }
        // 与 OptiScalerPage.PresetKey 同规则：opti_preset_<游戏>_<构建id>
        AppConfig.SetValue(name, $"opti_preset_{gameKey}_{buildId}");
        AppConfig.SetValue(name, OptiScalerPresets.FollowKey(gameKey));

        return OptiScalerPresets.Apply(buildDir, gameKey, name!)
            ? $"OptiScaler 配置 → {name}（已写入 profiles 并激活）"
            : $"✗ 套用配置失败（presets 目录里没有 {name}.ini？）";
    }

}
