using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Modules;

public sealed partial class ModulesPage
{
    private sealed record ModuleRowData(ModuleEntry Entry, IReadOnlyList<string> Versions,
        string? SelectedVersion, int? InjectDelay, int GlobalDefault, string? BridgeIniPath);
    private sealed record ModulePageData(IReadOnlyList<ModuleRowData> Rows, HashSet<string> UsedKeys);

    private readonly Dictionary<string, ModuleEntry> _resolvedEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _moduleLoadGate = new(1, 1);
    private HashSet<string> _usedKeys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _moduleLoadCancellation;
    private long _moduleLoadRevision;
    private long _moduleEditRevision;
    private bool _navigated;

    private async Task RefreshBridgeVersionAsync(ModuleChoiceItem item, ModuleEntry? entry, string? version)
    {
        GameId? gameId = _gameId;
        long editRevision = _moduleEditRevision;
        try
        {
            string? dll = await Task.Run(() => entry?.Definition is { } definition
                ? ModuleRegistry.ResolveDllPath(definition, version) : entry?.DllPath);
            if (!_navigated || gameId != _gameId || !_items.Contains(item)
                || !string.Equals(item.SelectedVersion, version, StringComparison.OrdinalIgnoreCase)) return;
            item.BridgeIniPath = dll is null ? null : Path.Combine(Path.GetDirectoryName(dll)!, FsrBridgeOptions.IniFileName);
            if (item.Expanded && editRevision == _moduleEditRevision) ReloadBridgeValues(item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resolve selected bridge version in background");
            if (_navigated && gameId == _gameId && _items.Contains(item))
                TextBlock_Status.Text = "桥配置读取失败：" + ex.Message;
        }
    }

    private void CancelModuleLoad()
    {
        _moduleLoadRevision++;
        _moduleLoadCancellation?.Cancel();
    }

    private async Task LoadModulesAsync()
    {
        _moduleLoadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _moduleLoadCancellation = cancellation;
        long revision = ++_moduleLoadRevision;
        long editRevision = _moduleEditRevision;
        GameId? gameId = _gameId;
        if (gameId is null)
        {
            ApplyModuleRows([]);
            cancellation.Dispose();
            if (ReferenceEquals(_moduleLoadCancellation, cancellation)) _moduleLoadCancellation = null;
            return;
        }

        var elapsed = Stopwatch.StartNew();
        TextBlock_Status.Text = "正在后台读取已安装模块，可以继续切换页面。";
        bool entered = false;
        try
        {
            await _moduleLoadGate.WaitAsync(cancellation.Token);
            entered = true;
            // 在工作线程完成迁移、catalog、磁盘解析和默认选择，await 前不做同步 I/O。
            ModulePageData data = await Task.Run(() => ReadModuleRows(gameId, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_navigated || revision != _moduleLoadRevision || gameId != _gameId) return;
            if (editRevision != _moduleEditRevision)
            {
                Load();
                return;
            }
            _usedKeys = data.UsedKeys;
            var apply = Stopwatch.StartNew();
            ApplyModuleRows(data.Rows);
            _logger.LogInformation("Modules page: backgroundMs={BackgroundMs:F1}, applyMs={ApplyMs:F1}, rows={Rows}",
                elapsed.Elapsed.TotalMilliseconds - apply.Elapsed.TotalMilliseconds, apply.Elapsed.TotalMilliseconds, data.Rows.Count);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load modules page in background");
            if (_navigated && revision == _moduleLoadRevision)
                TextBlock_Status.Text = "模块读取失败，可点击刷新重试：" + ex.Message;
        }
        finally
        {
            if (entered) _moduleLoadGate.Release();
            if (ReferenceEquals(_moduleLoadCancellation, cancellation)) _moduleLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    private static ModulePageData ReadModuleRows(GameId gameId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModuleRegistry.MigrateLegacyExtraInjectDll(gameId.GameBiz);
        string root = AppConfig.OptiScalerRootPath;
        if (AppConfig.GetUseOptiScalerLaunchOption(gameId)
            && string.IsNullOrWhiteSpace(AppConfig.GetSelectedOptiScalerId(gameId)) && root.Length > 0)
        {
            Features.OptiScaler.OptiScalerMigration.Run(gameId,
                new OptiScalerLibrary(root).GetSelected(includeDirectorySize: false, cancellationToken: cancellationToken));
        }
        ModuleCatalogFile.Apply(Features.Plugins.RemoteCatalogService.ModulesCachePath);
        List<ModuleEntry> entries = ModuleRegistry.List();
        // 未显式保存选择时，默认项只从这一遍解析结果推导，不再对每张卡调用 List()。
        var usedKeys = new HashSet<string>(AppConfig.GetUsedModuleKeysOrNull(gameId)
            ?? entries.Where(e => e.GloballyEnabled && !string.IsNullOrWhiteSpace(e.DllPath)).Select(e => e.Key).ToList(),
            StringComparer.OrdinalIgnoreCase);
        int globalDefault = AppConfig.GetDefaultInjectionDelaySeconds(gameId.GameBiz);
        var rows = new List<ModuleRowData>();
        foreach (ModuleEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.DllPath)) continue;
            IReadOnlyList<string> versions = entry.Definition is { } definition
                ? ModuleRegistry.InstalledTags(definition) : [];
            string? selected = entry.Definition is { } def ? AppConfig.GetModuleVersion(gameId, def.Id) : null;
            string? picked = selected;
            if (selected is null || !versions.Contains(selected, StringComparer.OrdinalIgnoreCase))
                selected = versions.FirstOrDefault();
            string? bridgeIni = null;
            if (IsFsrBridgeModule(entry))
            {
                string? dll = entry.Definition is { } bridge && !string.IsNullOrWhiteSpace(picked)
                    ? ModuleRegistry.ResolveDllPath(bridge, picked) : entry.DllPath;
                if (dll is not null && Path.GetDirectoryName(dll) is { Length: > 0 } directory)
                    bridgeIni = Path.Combine(directory, FsrBridgeOptions.IniFileName);
            }
            rows.Add(new ModuleRowData(entry, versions, selected,
                AppConfig.GetModuleInjectDelaySeconds(entry.Key), globalDefault, bridgeIni));
        }
        return new ModulePageData(rows, usedKeys);
    }
}
