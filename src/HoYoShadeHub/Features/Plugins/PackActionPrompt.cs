using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 覆盖包 auto.json 动作的同意弹窗：列出**全部**动作和步骤，
/// 高危动作（覆盖文件 / 注册表加游戏 / 清 ini / 删滤镜）标红并展开全部文件清单。
/// 逐项勾选、默认全装；「全部不要」= 同意但全禁（包内容再变之前不再弹）。
/// 导入覆盖包时（<see cref="OverlayPackActionPrompter"/>）和每游戏插件页兜底共用。
/// </summary>
public static class PackActionConsentDialog
{
    private static int _showing;

    /// <param name="gameNames">这批动作会落到哪些游戏上（导入时可能一次多个服）；null = 当前游戏</param>
    /// <returns>null = 下次再说；否则保留每个原始步骤的勾选结果。</returns>
    public static async Task<PackStepSelection?> ShowAsync(XamlRoot xamlRoot, string packRoot, List<PackAutoAction> actions, string? gameNames = null)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _showing, 1, 0) != 0) return null;
        try { return await ShowCoreAsync(xamlRoot, packRoot, actions, gameNames); }
        finally { System.Threading.Interlocked.Exchange(ref _showing, 0); }
    }

    private static async Task<PackStepSelection?> ShowCoreAsync(XamlRoot xamlRoot, string packRoot,
        List<PackAutoAction> actions, string? gameNames)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "勾选要应用的项目（默认全部勾选）。未勾选的项目现在及以后启动时都不会执行："
                   + (string.IsNullOrWhiteSpace(gameNames) ? string.Empty : $"\n影响的游戏：{gameNames}"),
            TextWrapping = TextWrapping.Wrap,
        });

        var boxes = new List<(PackAutoAction Action, int Index, CheckBox Box)>();
        foreach (PackAutoAction action in actions)
        {
            panel.Children.Add(new TextBlock
            {
                Text = action.Name + (action.RunOnLaunch ? "　（每次启动前自动检查）" : string.Empty),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });

            for (int index = 0; index < action.Steps.Count; index++)
            {
                PackActionStep step = action.Steps[index];
                bool highRisk = PackActionDescriber.IsHighRisk(step);
                var content = new StackPanel { Spacing = 2 };
                var stepText = new TextBlock
                {
                    Text = $"{index + 1}. {(highRisk ? "⚠ " : string.Empty)}{PackActionDescriber.Describe(step, packRoot)}",
                    TextWrapping = TextWrapping.Wrap,
                };
                if (highRisk)
                {
                    stepText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                }

                content.Children.Add(stepText);

                foreach (string detail in PackActionDescriber.HighRiskDetails(step, packRoot))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "　　· " + detail,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                var box = new CheckBox { IsChecked = true, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                boxes.Add((action, index, box));
                panel.Children.Add(box);
            }
        }

        var scroll = new ScrollViewer { Content = panel, MaxHeight = 420 };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "覆盖包动作确认",
            Content = scroll,
            PrimaryButtonText = "应用（3 秒）",
            IsPrimaryButtonEnabled = false,
            SecondaryButtonText = "全部不要",
            CloseButtonText = "下次再说",
            DefaultButton = ContentDialogButton.Primary,
            MaxWidth = 560,
        };

        var delay = new ConfirmationDelay();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        void UpdateCountdown()
        {
            dialog.IsPrimaryButtonEnabled = delay.CanApply;
            dialog.PrimaryButtonText = delay.CanApply ? "应用" : $"应用（{delay.RemainingSeconds} 秒）";
            if (delay.CanApply) timer.Stop();
        }
        timer.Tick += (_, _) => UpdateCountdown();
        dialog.Opened += (_, _) => { delay.Start(); UpdateCountdown(); timer.Start(); };
        dialog.Closed += (_, _) => timer.Stop();
        dialog.PrimaryButtonClick += (_, args) => { if (!delay.CanApply) args.Cancel = true; };
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { timer.Stop(); }
        if (result == ContentDialogResult.None) return null;
        bool Declined((PackAutoAction Action, int Index, CheckBox Box) row) =>
            result == ContentDialogResult.Secondary || row.Box.IsChecked != true;
        return new PackStepSelection(
            new(actions.Where(action => boxes.Where(row => ReferenceEquals(row.Action, action)).All(Declined))
                .Select(action => action.Name), StringComparer.Ordinal),
            new(boxes.Where(Declined).Select(row => PackStepSelection.Key(row.Action, row.Index)), StringComparer.Ordinal));
    }
}

/// <summary>给 auto.json 动作执行造上下文 + 跑一组动作（导入时和插件页共用）</summary>
public static class PackActionExecutor
{
    /// <summary>
    /// 造一份动作上下文：插件服务能建就建（读不出 ini 就只跑不依赖 ini 的步骤）。
    /// gameId 不能为空 —— 启动选项 / OptiScaler 选择这类步骤都按它记账。
    /// </summary>
    public static LauncherActionContext BuildContext(
        GameId gameId, GameEntry? entry, ShadeHost? host, string? packRoot,
        XamlRoot? xamlRoot, bool interactive, Action<string>? report, PackAutoActionSnapshot snapshot)
    {
        GamePluginService? plugins = null;
        if (entry is not null)
        {
            try
            {
                plugins = GamePluginServiceFactory.Create(entry, host);
            }
            catch
            {
                // 读不出插件状态就只跑不依赖 ini 的步骤
            }
        }

        return new LauncherActionContext
        {
            GameId = gameId,
            GameBiz = gameId.GameBiz,
            Entry = entry,
            Host = host,
            Plugins = plugins,
            PackRoot = packRoot,
            PackSnapshot = snapshot,
            ExpectedPackHash = snapshot.Hash,
            XamlRoot = xamlRoot,
            Interactive = interactive,
            Report = report,
        };
    }

    /// <summary>逐个执行没被禁用的动作，返回每个动作的一行摘要</summary>
    public static async Task<List<PackActionOncePolicy.ExecutionResult>> RunAsync(
        IEnumerable<PackAutoAction> actions, PackStepSelection selection, LauncherActionContext context)
    {
        List<PackActionOncePolicy.ExecutionResult> batch = await PackActionOncePolicy.RunBatchAsync(
            actions.Where(selection.HasEnabledSteps),
            async action =>
            {
                var result = await LauncherActionRunner.RunResultAsync(action, context, isPackAction: true);
                return result with { Summary = $"「{action.Name}」：{result.Summary}" };
            });
        return batch;
    }
}

public sealed record PackImportActionResult(string Summary, bool HasFailures = false);

/// <summary>
/// 导入覆盖包后的「动作一并办掉」：扫所有已装游戏的包目录，auto.json 还没同意过的
/// 现在弹窗；确认后**立即**对这些游戏执行（once 步骤记 .hysx_once.json，启动前自动跑会跳过）。
///
/// <para>
/// 游戏已装但 cache\games\ 目录还没生成（全新便携包首导）时，安装器把 GamePack 暂存在
/// .pending-gamepacks\&lt;hint&gt; —— 这里按「已装游戏」反查暂存并先铺进包目录，
/// 保证弹窗在导入当场就弹，不用等插件页 / 启动。游戏真没装的不管：游戏出现后插件页兜底。
/// 导入时先绑定游戏 INI，再独立确认 auto.json；没有动作或暂不执行动作也能找到插件。
/// </para>
/// </summary>
public static class OverlayPackActionPrompter
{
    private static readonly ILogger _logger = AppConfig.GetLogger<GamePluginPage>();

    /// <returns>结构化执行结果；没有待办或全部延后时摘要为空。</returns>
    public static async Task<PackImportActionResult> PromptAndRunAsync(XamlRoot xamlRoot, ShadeHost? host, Action<string>? report,
        IReadOnlyCollection<string>? refreshGameHints = null, bool refreshReimportedPack = false)
    {
        // 按 auto.json 内容 hash 分组：同一份包内容（多个服共用一份）只弹一次窗
        var groups = new Dictionary<string, (string PackRoot, string Hash, List<PackAutoAction> Actions,
            List<(GameId Id, GameEntry Entry, PackAutoActionSnapshot Snapshot)> Games)>();
        var summaries = new List<string>();
        bool hasFailures = false;

        string cacheRoot = AppConfig.CacheRoot;
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return new(string.Empty);
        }

        try
        {
            GameDiscoveryService discovery = GameCatalog.CreateService();

            // 候选游戏 = 已知 biz 里装了的 + 自定义游戏条目（目录真的存在才算）
            var candidates = new List<(GameId Id, GameEntry Entry)>();
            foreach (GameBiz biz in GameBiz.AllGameBizs)
            {
                if (GameId.FromGameBiz(biz) is not { } id)
                {
                    continue;
                }

                GameEntry? entry = GameCatalog.GetOrCreate(discovery, id);
                if (entry?.GameDirectory is { } dir && Directory.Exists(dir))
                {
                    candidates.Add((id, entry));
                }
            }

            foreach (GameEntry custom in GameCatalog.CustomEntries())
            {
                if (custom.GameDirectory is { } dir && Directory.Exists(dir))
                {
                    candidates.Add((new GameId { Id = custom.CustomBizValue, GameBiz = custom.CustomBiz }, custom));
                }
            }

            foreach ((GameId id, GameEntry entry) in candidates)
            {
                string gameKey = id.GameBiz.Value;
                if (refreshGameHints is { Count: > 0 }
                    && !refreshGameHints.Any(hint => gameKey.StartsWith(hint, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string packRoot = GameAddonPack.PackDirectory(cacheRoot, gameKey);

                if (refreshReimportedPack && refreshGameHints is { Count: > 0 })
                {
                    // A successful second import of the same GamePack is a new
                    // user-authorized batch, even when auto.json has the same hash.
                    PackOnceLedger.ResetForReimport(packRoot);
                    PackActionConsent.ResetForReimport(gameKey);
                    _logger.LogInformation("覆盖包重新导入：已刷新 {Game} 的 auto.json 同意和 once 账本", gameKey);
                }

                // 暂存的 GamePack 先铺进包目录（同 GameAddonPackService.Sync 的暂存逻辑）：
                // 全新实例导入时 cache\games\ 还没有这个游戏的目录，不铺的话弹窗永远不会在导入当场弹
                try { PackPendingMigration.PlaceMatching(cacheRoot, gameKey); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _logger.LogWarning(ex, "Place pending pack before consent ({Game})", gameKey);
                    summaries.Add($"【{entry.DisplayName}】暂存覆盖包迁移失败，未执行动作：{ex.Message}");
                    hasFailures = true;
                    continue;
                }

                if (refreshReimportedPack && GameAddonPackUserContent.HasAny(packRoot))
                {
                    try
                    {
                        // 拖入已授权安装与路径绑定；auto.json 的授权只控制额外动作。
                        // 必须放在读取/分组动作之前，无 auto.json、已同意、延后执行都要绑定。
                        string boundAddonPath = await Task.Run(() =>
                        {
                            string addonDirectory = GameAddonPackService.Sync(id, null, host)
                                ?? throw new IOException("无法铺设覆盖包插件目录，请检查当前 ReShade 宿主。");
                            return GamePackIniBinding.Bind(entry, host, addonDirectory);
                        });
                        _logger.LogInformation("覆盖包导入已绑定 {Game}：{Ini} → {AddonPath}", gameKey, entry.ReShadeIniPath, boundAddonPath);
                        summaries.Add($"【{entry.DisplayName}】已绑定 ReShade.ini 的 AddonPath → {boundAddonPath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Bind imported pack before actions ({Game})", gameKey);
                        summaries.Add($"【{entry.DisplayName}】覆盖包文件已安装，但 INI 绑定失败：{ex.Message}");
                        hasFailures = true;
                        continue;
                    }
                }

                PackAutoActionSnapshot? snapshot = PackAutoActionSnapshot.TryLoad(packRoot);
                if (snapshot is null)
                {
                    continue;
                }
                List<PackAutoAction> actions = snapshot.LoadActions();
                string hash = snapshot.Hash;
                if (PackActionConsent.IsConsented(gameKey, hash))
                {
                    continue;
                }

                if (!groups.TryGetValue(hash, out var group))
                {
                    group = (packRoot, hash, actions, []);
                    groups[hash] = group;
                }

                group.Games.Add((id, entry, snapshot));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scan pack actions on import");
            summaries.Add("扫描覆盖包动作失败，未继续执行：" + ex.Message);
            return new(string.Join("\n\n", summaries), true);
        }

        if (groups.Count == 0)
        {
            return new(string.Join("\n\n", summaries), hasFailures);
        }

        foreach (var (packRoot, hash, actions, games) in groups.Values)
        {
            string gameNames = string.Join("、", games.Select(g => g.Entry.DisplayName));
            PackStepSelection? selection;
            try
            {
                selection = await PackActionConsentDialog.ShowAsync(xamlRoot, packRoot, actions, gameNames);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pack action consent prompt on import");
                summaries.Add($"【{gameNames}】动作确认失败，未执行：{ex.Message}");
                hasFailures = true;
                continue;
            }

            if (selection is null)
            {
                continue;   // 下次再说：不记同意，插件页仅显示显式确认入口
            }

            foreach ((GameId id, GameEntry entry, PackAutoActionSnapshot snapshot) in games)
            {
                try
                {
                    // 分组只共享弹窗，不共享账本；每个游戏必须使用自己的包根。
                    string gamePackRoot = GameAddonPack.PackDirectory(cacheRoot, id.GameBiz.Value);
                    if (snapshot.Hash != hash || !snapshot.IsCurrent(gamePackRoot))
                    {
                        summaries.Add($"【{entry.DisplayName}】包内容已变化，需显式重新确认；未执行。");
                        hasFailures = true;
                        continue;
                    }
                    PackActionConsent.Save(id.GameBiz.Value, hash, selection);

                    // 首次安装必须先取得可用目标；失败时停在副作用/once reservation 之前。
                    GameLaunchIniPreparation.EnsurePackActionTarget(entry, host,
                        snapshot.LoadActions().Where(selection.HasEnabledSteps), selection.IsEnabled);

                    // 先把这个游戏的 ini 定向好、包 Addons 铺好，再跑动作：
                    // set_addons 按 AddonPath 目录扫文件写 [ADDON] DisabledAddons，
                    // 目录还没指向包 / 包还没铺时「禁用全部」会扫到空目录或旧目录，白跑一次
                    // （once 记账一立，状态就定格在「全开」——崩铁 mfgunlock 事故）
                    try
                    {
                        GameIniBootstrap.Ensure(entry, host, GameLauncher.GameLauncherService.UsesGenshinFinalDx12(id));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ensure game ini before pack actions ({Game})", id.GameBiz.Value);
                    }

                    try
                    {
                        GameAddonPackService.Sync(id, entry, host);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Sync addon pack before pack actions ({Game})", id.GameBiz.Value);
                    }

                    LauncherActionContext context = PackActionExecutor.BuildContext(
                        id, entry, host, gamePackRoot, xamlRoot, interactive: true, report, snapshot);
                    List<PackActionOncePolicy.ExecutionResult> results = await PackActionExecutor.RunAsync(snapshot.LoadActions(), selection, context);
                    hasFailures |= results.Any(result => result.Blocked);

                    // auto.json 可能在最后一步写入旧的相对 AddonPath。覆盖包导入的最终状态
                    // 必须仍然指向本游戏的专属包，否则插件文件虽已落盘，游戏页会扫描到空目录。
                    string boundAddonPath = GamePackIniBinding.Bind(entry, host, GameAddonPack.AddonDirectory(cacheRoot, id.GameBiz.Value));
                    _logger.LogInformation("覆盖包动作后已复核绑定 {Game}：{Ini} → {AddonPath}", id.GameBiz.Value, entry.ReShadeIniPath, boundAddonPath);
                    if (results.Count > 0)
                    {
                        summaries.Add($"【{entry.DisplayName}】\n" + string.Join("\n", results.Select(result => result.Summary)));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Run pack actions on import for {Game}", id.GameBiz.Value);
                    summaries.Add($"【{entry.DisplayName}】执行失败：{ex.Message}");
                    hasFailures = true;
                }
            }
        }

        return new(string.Join("\n\n", summaries), hasFailures);
    }

}
