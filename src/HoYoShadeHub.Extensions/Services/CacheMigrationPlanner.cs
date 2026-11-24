namespace HoYoShadeHub.Extensions.Services;

/// <summary>一条要搬迁的老存储（<paramref name="Source"/> → <paramref name="Target"/>）</summary>
public sealed record LegacyStoreMove(string Kind, string Source, string Target)
{
    /// <summary>目标已经存在：不能覆盖，改成「两边都留」并把源目录原地保留</summary>
    public bool TargetExists { get; init; }
}

/// <summary>
/// 「版本更新引导」的纯决策部分：该不该弹引导、老存储要往哪搬。
/// 只读盘、不写盘，方便离线断言。
/// </summary>
public static class CacheMigrationPlanner
{
    public const string OptiScalerKind = "optiscaler";

    public const string ModulesKind = "modules";
    public const string PortableLayoutFileName = "portable-layout.json";

    /// <summary>Explicit current full-package layout; does not suppress prompts for unmarked legacy installs.</summary>
    public static bool HasSupportedPortableLayout(string userDataRoot)
    {
        try
        {
            if (!File.Exists(Path.Combine(userDataRoot, ".portable"))
                || !(File.Exists(Path.Combine(userDataRoot, "F6Launcher.exe"))
                     || File.Exists(Path.Combine(userDataRoot, "HoYoShadeHub.exe")))) return false;
            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(userDataRoot, PortableLayoutFileName)));
            var marker = document.RootElement;
            return marker.GetProperty("schema").GetInt32() == 1
                && marker.GetProperty("layout").GetString() == "full-portable-v1"
                && marker.GetProperty("moduleRoot").GetString() == "Modules"
                && marker.GetProperty("optiscalerRoot").GetString() == "OptiScaler"
                && marker.GetProperty("cacheRoot").GetString() == "cache";
        }
        catch { return false; }
    }


    /// <summary>
    /// 要不要弹「版本更新引导」：
    /// 老布局还在（装过插件但归档是空的 / OptiScaler、Modules 还只在老位置），
    /// 且没跑过 / 没有跳过标记。
    /// </summary>
    public static bool NeedsMigration(
        bool cacheMigrated,
        bool hasInstalledExtensions,
        bool pluginStoreHasVersions,
        bool legacyOptiScalerExists,
        bool cacheOptiScalerExists,
        bool legacyModulesExists,
        bool cacheModulesExists,
        bool supportedPortableLayout = false)
    {
        // 注意：OptiScaler **不参与迁移**（用户明确要求）—— 它自己的库
        // <基准>\OptiScaler\<来源>\<版本>\ 本来就多版本共存，原地用即可。
        // 两个 OptiScaler 参数保留只是为了不动调用方。
        _ = legacyOptiScalerExists;
        _ = cacheOptiScalerExists;

        if (cacheMigrated || supportedPortableLayout)
        {
            return false;
        }

        return hasInstalledExtensions && !pluginStoreHasVersions;
    }

    /// <summary>规划要搬迁的老存储（只列出**源目录真的存在**且和目标不是同一个的那些）。</summary>
    public static List<LegacyStoreMove> PlanMoves(
        string? legacyOptiScaler,
        string? legacyModules,
        string? cacheOptiScaler,
        string? cacheModules)
    {
        return [];
    }
}
