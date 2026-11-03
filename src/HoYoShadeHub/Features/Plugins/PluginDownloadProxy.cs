using HoYoShadeHub.Extensions.Networking;
using HoYoShadeHub.Helpers;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>将统一下载设置应用到扩展栈；仅使用直连与独立公共 GitHub 代理。</summary>
public static class PluginDownloadProxy
{
    /// <summary>
    /// 用户选「自动选择」时先用的那个（还没测速完 / 测速全失败时兜底）。
    /// 真正的自动选择见 <see cref="ApplyAutoAsync"/>。
    /// </summary>
    private const int AutoFallbackServer = 0;

    /// <summary>
    /// 自动选择时逐个测速的顺序（0 = GitHub 直连优先）。
    /// 4 / 5 = gh-proxy.org、ghfast.top，是仅有的两个能转发 raw 域名的公共代理。
    /// </summary>
    private static readonly int[] AutoCandidates = [0, 4, 5];

    /// <summary>本次会话内自动选过一次就记住，不用每次进页面都测速</summary>
    private static int? _autoPickedServer;

    /// <summary>测速结果的说明（界面上显示「自动选择（GitHub 直连 123ms）」用）</summary>
    public static string? AutoPickReason { get; private set; }

    /// <summary>Resolved prefix currently applied to plugin downloads; null means GitHub direct.</summary>
    public static string? CurrentProxyUrl { get; private set; }

    public static int CurrentServerIndex { get; private set; }

    /// <summary>True when plugin downloads are going through a mirror.</summary>
    public static bool IsProxied => !string.IsNullOrWhiteSpace(CurrentProxyUrl);

    /// <summary>给界面看的当前线路名称（之前这里是乱码，全是问号）</summary>
    public static string DescribeRoute() => CurrentServerIndex switch
    {
        -1 => "自动选择",
        0 => "GitHub 直连",
        4 => IsProxied ? "gh-proxy.org" : "gh-proxy.org（代理未生效，走直连）",
        5 => IsProxied ? "ghfast.top" : "ghfast.top（代理未生效，走直连）",
        _ => "GitHub 直连",
    };

    /// <summary>
    /// Reads the setting and pushes it into <see cref="HysxHttp.ProxyUrl"/>.
    /// Call this before any plugin catalog / download work.
    /// </summary>
    public static void Apply() => Apply(HoYoShadeHub.AppConfig.HoYoShadeFrameworkDownloadServer);

    public static void Apply(int serverIndex)
    {
        serverIndex = serverIndex is 1 or 2 or 3 ? 0 : serverIndex;
        CurrentServerIndex = serverIndex;

        int effective = serverIndex;
        if (serverIndex == -1)
        {
            effective = AutoFallbackServer;
        }

        string? proxyUrl;
        try
        {
            proxyUrl = CloudProxyManager.GetProxyUrl(effective);
        }
        catch (Exception)
        {
            proxyUrl = null;
        }

        CurrentProxyUrl = string.IsNullOrWhiteSpace(proxyUrl) ? null : proxyUrl.TrimEnd('/');
        HysxHttp.ProxyUrl = CurrentProxyUrl;
    }

    /// <summary>
    /// 「自动选择」：真的去给每个候选服务器测一下延迟，挑最快的那个用。
    ///
    /// <para>
    /// 之前「自动选择」是<b>硬编码走 Cloudflare</b>，根本没测速 —— 用户选了自动，
    /// 实际可能连的是最慢的那条线。现在按延迟挑。
    /// </para>
    ///
    /// <para>
    /// 测速失败 / 全部超时就退回 <see cref="AutoFallbackServer"/>，不会因为测速把下载卡住。
    /// 结果在本次会话内缓存，避免每次进页面都重测。
    /// </para>
    /// </summary>
    public static async Task ApplyAutoAsync(HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        // 本次会话已经挑过了，直接用
        if (_autoPickedServer is { } picked)
        {
            Apply(picked);
            return;
        }

        // 先把设置按老规则用上，保证测速期间就算出问题也能下载
        Apply(-1);

        try
        {
            int best = AutoFallbackServer;
            long bestLatency = long.MaxValue;

            foreach (int candidate in AutoCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long latency = await CloudProxyManager.PingServerAsync(candidate, httpClient)
                    .ConfigureAwait(false);

                if (latency >= 0 && latency < bestLatency)
                {
                    bestLatency = latency;
                    best = candidate;
                }
            }

            _autoPickedServer = best;
            AutoPickReason = bestLatency == long.MaxValue
                ? "测速都没通，先用默认线路"
                : $"{DescribeServer(best)}（{bestLatency}ms）";

            Apply(best);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 测速出问题不该影响下载 —— 保持 Apply(-1) 的兜底结果
            _autoPickedServer = AutoFallbackServer;
            AutoPickReason = "测速失败，先用默认线路";
        }
    }

    /// <summary>服务器序号 → 显示名（给测速结果文案用）</summary>
    private static string DescribeServer(int serverIndex) => DownloadServerCatalog.Describe(serverIndex);

    /// <summary>忘掉本次会话的自动选择结果（设置里改了服务器时调）</summary>
    public static void ResetAutoPick()
    {
        _autoPickedServer = null;
        AutoPickReason = null;
    }
}