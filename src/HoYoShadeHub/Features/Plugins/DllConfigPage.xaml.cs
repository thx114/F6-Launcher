using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Features.OptiScaler;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 「DLL 配置」页（左侧一级导航）。
///
/// <para>
/// 为什么需要单独一页：DLSS5 那几个插件光有 <c>.addon64</c> 是跑不起来的 —— 还要一整套运行时
/// 躺在**同一个目录**里（ReShade 只从 addons 目录加载）：
/// </para>
/// <list type="bullet">
/// <item><c>nvngx_dlssnr.dll</c> —— DLSS5 的神经渲染运行时，缺了插件直接加载不了（必需）；</item>
/// <item><c>sl.*.dll</c> —— Streamline 一整套，缺了大概率不出画面（建议）。</item>
/// </list>
///
/// <para>
/// 清单来自用户指定的 <c>RankFTW/RHI</c> 的 <c>dlss_manifest.json</c>（见 docs/RESHADE-INI.md §4），
/// 下载后解压进 <c>&lt;HoYoShade&gt;\reshade-shaders\Addons</c>。
/// </para>
/// </summary>
public sealed partial class DllConfigPage : PageBase
{
    private readonly ILogger<DllConfigPage> _logger = AppConfig.GetLogger<DllConfigPage>();

    private ShadeHost? _host;
    private DllCatalog? _catalog;
    // 页面可以在旧 I/O 完成前卸载/重建；跨实例串行访问共享 DLL 库。
    private bool _working;
    private long _operationGeneration;
    private long _operationRevision;
    private long _progressRevision;
    private TaskCompletionSource? _workCompletion;
    private bool _isWorking
    {
        get => _working;
        set
        {
            _working = value;
            _operationRevision++;
            if (value)
            {
                _workCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _operationGeneration = PageGeneration;
                _nrScanRevision++;
            }
            else _workCompletion?.TrySetResult();
            if (!IsLoaded) return;
            Control_Actions.IsEnabled = !value;
            Control_Content.IsEnabled = !value;
            if (!value)
            {
                ProgressBar_Overall.IsIndeterminate = false;
                ProgressBar_Overall.Visibility = Visibility.Collapsed;
            }
        }
    }
    private bool _buildingCards;
    private bool _scanningNr;
    private long _nrScanRevision;
    private long _familyRevision;
    private DispatcherTimer? _nrStateTimer;
    private string _nrDiskState = string.Empty;
    private readonly HashSet<string> _nrUpdates = new(StringComparer.OrdinalIgnoreCase);

    public DllConfigPage()
    {
        InitializeComponent();

        // 组件族列表错峰入场（和其它页面同一套 500ms + 45ms）
        Loaded += (_, _) =>
        {
            long generation = PageGeneration;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsPageGenerationCurrent(generation)) MotionAnimations.PlayListEntrance(ItemsControl_Families);
            });
        };
    }

    public ObservableCollection<InstalledDllViewModel> Installed { get; } = [];

    public ObservableCollection<DllFamilyViewModel> Families { get; } = [];
    public ObservableCollection<InstalledDllViewModel> OtherInstalled { get; } = [];
    public ObservableCollection<NrVersionCardViewModel> NrVersions { get; } = [];

    protected override void OnLoaded()
    {
        Control_Actions.IsEnabled = !_isWorking;
        Control_Content.IsEnabled = !_isWorking;

        // 先把「下载服务器」这个设置推给 HysxHttp，再去拉组件清单。
        // 不推的话 ProxyUrl 是 null，清单会直连 raw.githubusercontent.com ——
        // 国内基本连不上，报出来就是一句 SSL connection could not be established。
        PluginDownloadProxy.Apply();

        _nrStateTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _nrStateTimer.Tick -= RefreshNrDiskState;
        _nrStateTimer.Tick += RefreshNrDiskState;
        _nrStateTimer.Start();

        _ = RefreshOnLoadAsync();
    }

    private async Task RefreshOnLoadAsync()
    {
        long generation = PageGeneration;
        // 旧页面任务仍在完成链接时不开始第二次操作，也不丢掉重新进入页面的刷新。
        if (_isWorking && _workCompletion is { } completion) await completion.Task;
        if (IsPageGenerationCurrent(generation)) await RefreshAsync();
    }

    private bool IsOperationCurrent => IsPageGenerationCurrent(_operationGeneration);
    private void EnsureOperationCurrent()
    {
        if (_isWorking && !IsOperationCurrent) throw new OperationCanceledException("DLL 页面已卸载。");
    }

    private async Task<T> RunDiskAsync<T>(Func<T> action)
    {
        var result = await NrRuntimeWorkQueue.RunAsync(action);
        EnsureOperationCurrent();
        return result;
    }

    private Task RunDiskAsync(Action action) => RunDiskAsync(() => { action(); return true; });

    private async Task<T> RunDiskTaskAsync<T>(Func<Task<T>> action)
    {
        var result = await NrRuntimeWorkQueue.RunTaskAsync(action);
        EnsureOperationCurrent();
        return result;
    }

    private async Task<T> RunBackgroundAsync<T>(Func<Task<T>> action)
    {
        // 目录联网不占用 NR 文件事务锁，避免慢网络阻塞其他页面使用已有版本。
        var result = await Task.Run(action);
        EnsureOperationCurrent();
        return result;
    }

    protected override void OnUnloaded()
    {
        _nrStateTimer?.Stop();
        _nrScanRevision++;
        _familyRevision++;
        Installed.Clear();
        Families.Clear();
        OtherInstalled.Clear();
        NrVersions.Clear();
    }

    #region 刷新

    private async Task RefreshAsync()
    {
        if (_isWorking)
        {
            return;
        }

        _isWorking = true;
        ProgressBar_Overall.IsIndeterminate = true;
        ProgressBar_Overall.Visibility = Visibility.Visible;
        TextBlock_Status.Text = "正在读插件目录与组件清单…";
        TextBlock_RuntimeOk.Visibility = Visibility.Collapsed;

        try
        {
            var resolved = await RunDiskAsync(() =>
            {
                var host = PluginHostLocator.Resolve(out string reason);
                return (Host: host, Reason: reason);
            });
            _host = resolved.Host;

            if (_host is null)
            {
                Installed.Clear();
                Families.Clear();
                TextBlock_TargetPath.Text = "未找到 HoYoShade 目录";
                ShowInfo("没有可管理的 HoYoShade 目录", resolved.Reason, InfoBarSeverity.Warning);
                TextBlock_Status.Text = string.Empty;
                return;
            }

            TextBlock_TargetPath.Text = _host.AddonsPath;
            // 本地版本先显示，云端刷新失败或耗时不应让已下载内容消失。
            // 文件系统是已下载状态的依据；刷新不能把用户手动删除的文件恢复出来。
            await RefreshNrCardsAsync();
            await RunBackgroundAsync(async () => { await RemoteCatalogService.RefreshIfDueAsync(); return true; });
            await LoadInstalledAsync();

            _catalog ??= await RunBackgroundAsync(() => DllComponentCatalog.LoadAsync());
            await BuildFamiliesAsync();

            if (_catalog.Error is { } error)
            {
                ShowInfo("拉组件清单失败（还能用内置的那几条）", error, InfoBarSeverity.Warning);
            }
            else
            {
                await UpdateStatusInfoAsync();
            }

            _logger.LogInformation("DLL page: {Installed} dlls, {Families} families, host = {Host}",
                Installed.Count, Families.Count, _host.RootPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh DLL page");
            if (IsOperationCurrent) TextBlock_Status.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            _isWorking = false;
        }
    }

    private async Task LoadInstalledAsync()
    {
        string? directory = _host?.AddonsPath;
        long generation = PageGeneration;
        var dlls = await RunDiskAsync(() => DllInstaller.Scan(directory));
        if (!IsPageGenerationCurrent(generation)) return;

        Installed.Clear();
        foreach (InstalledDll dll in dlls)
        {
            Installed.Add(new InstalledDllViewModel(dll));
        }


    }

    private sealed record FamilySnapshot(DllFamily Family, DllComponent[] Components,
        InstalledDll[] Installed, DllVariantProbe Probe, StoredDllVersion[] Archived);

    private async Task BuildFamiliesAsync()
    {
        long generation = PageGeneration;
        long revision = ++_familyRevision;
        var catalog = _catalog;
        var installed = Installed.Select(i => i.Dll).ToArray();
        var selected = Families.ToDictionary(f => f.Family.Id, f => f.SelectedVersion, StringComparer.OrdinalIgnoreCase);
        // Identify 会对大 DLL 求 SHA256，版本枚举也访问磁盘；这里只回传数据，不在 worker 创建 WinUI 对象。
        var snapshots = await RunDiskAsync(() =>
        {
            if (catalog is null) return Array.Empty<FamilySnapshot>();
            var store = new DllVersionStore(AppConfig.CacheRoot);
            var result = new List<FamilySnapshot>();
            foreach (var family in DllComponentCatalog.Families)
            {
                if (family.Id.Equals("dlssnr", StringComparison.OrdinalIgnoreCase)) continue;
                var archived = store.ListVersions(family.Id).ToArray();
                var remote = catalog.Of(family.Id);
                var components = remote.Concat(archived
                    .Where(v => !remote.Any(c => c.Version.Equals(v.Version, StringComparison.OrdinalIgnoreCase)))
                    .Select(v => new DllComponent(family.Id, v.Version, ""))).ToArray();
                if (components.Length == 0) continue;
                var probe = DllVariantResolver.Identify(store, family,
                    installed.FirstOrDefault(d => DllInstaller.Matches(d.FileName, family)),
                    AppConfig.GetInstalledDllVariant(family.Id), AppConfig.GetInstalledDllVariantSize(family.Id));
                result.Add(new FamilySnapshot(family, components, installed, probe, archived));
            }
            return result.ToArray();
        });
        if (!IsPageGenerationCurrent(generation) || revision != _familyRevision) return;
        Families.Clear();
        foreach (var snapshot in snapshots)
        {
            var family = snapshot.Family;
            var vm = new DllFamilyViewModel(family, snapshot.Components, snapshot.Installed, probe: snapshot.Probe);
            if ((!vm.HasInstalled || vm.IsCurrentKnown)
                && selected.TryGetValue(family.Id, out string? previous) && vm.Versions.Contains(previous))
                vm.SelectedVersion = previous;
            else if (!vm.HasInstalled && DllSelectionPolicy.SelectDefault(family, snapshot.Components,
                OptiScalerPresetCatalog.GpuClass) is { } defaultComponent) vm.SelectComponent(defaultComponent);
            vm.SetInstalledVersions(snapshot.Archived
                .OrderByDescending(v => v.Version.Contains("Lecram", StringComparison.OrdinalIgnoreCase))
                .Select(v => new InstalledDllVersionRow(family.Id, v.Version, v.Version,
                    vm.IsCurrentKnown && string.Equals(v.Version, vm.CurrentVersion, StringComparison.OrdinalIgnoreCase))));
            if (vm.HasInstalled && !vm.InstalledVersionRows.Any(r => r.IsCurrent))
                vm.InstalledVersionRows.Insert(0, new InstalledDllVersionRow(family.Id,
                    vm.CurrentVersion ?? "未知版本", vm.IsCurrentKnown ? vm.CurrentVersion : "PE " + vm.CurrentVersion + "（变体未确认）", true, false));
            Families.Add(vm);
        }
        OtherInstalled.Clear();
        foreach (var dll in Installed.Where(d => !HysxFileLink.IsNeuralRuntime(d.FileName)
            && !snapshots.Any(f => DllInstaller.Matches(d.FileName, f.Family)))) OtherInstalled.Add(dll);
        await RefreshNrCardsAsync();
    }

    private sealed record NrCardSnapshot(string Version, DllComponent? Component, bool Installed,
        bool Available, bool Update);
    private sealed record NrLibrarySnapshot(NrCardSnapshot[] Cards, string DiskState);

    private async Task RefreshNrCardsAsync(bool onlyIfChanged = false)
    {
        long generation = PageGeneration;
        long revision = ++_nrScanRevision;
        var components = _catalog?.Of("dlssnr").ToArray() ?? [];
        var updates = new HashSet<string>(_nrUpdates, StringComparer.OrdinalIgnoreCase);
        var snapshot = await RunDiskAsync(() =>
        {
            var store = new DllVersionStore(AppConfig.CacheRoot);
            // 一轮只枚举一次，不对每张卡片反复 FilePath/ListVersions。
            var installed = store.ListVersions("dlssnr")
                .Where(v => File.Exists(Path.Combine(v.Directory, NrRuntimeLibrary.FileName)))
                .Select(v => v.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var versions = DllFingerprintCatalog.Fingerprints
                .Where(f => f.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase)).Select(f => f.Version)
                .Concat(components.Select(c => c.Version)).Concat(installed)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(installed.Contains);
            var cards = versions.Select(version => new NrCardSnapshot(version,
                components.FirstOrDefault(c => c.Version.Equals(version, StringComparison.OrdinalIgnoreCase)),
                installed.Contains(version), AppConfig.IsNrVersionEnabled(version), updates.Contains(version))).ToArray();
            return new NrLibrarySnapshot(cards, string.Join("\n", installed.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)));
        });
        if (!IsPageGenerationCurrent(generation) || revision != _nrScanRevision
            || (onlyIfChanged && (_isWorking || snapshot.DiskState == _nrDiskState))) return;
        _buildingCards = true;
        try
        {
            NrVersions.Clear();
            bool downloadHeadingShown = false;
            foreach (var card in snapshot.Cards)
            {
                NrVersions.Add(new NrVersionCardViewModel(card.Version, card.Component, card.Installed,
                    card.Available, card.Update, !card.Installed && !downloadHeadingShown));
                if (!card.Installed) downloadHeadingShown = true;
            }
            _nrDiskState = snapshot.DiskState;
        }
        finally { _buildingCards = false; }
    }

    private async void RefreshNrDiskState(object? sender, object e)
    {
        if (_isWorking || _buildingCards || _scanningNr) return;
        _scanningNr = true;
        try { await RefreshNrCardsAsync(onlyIfChanged: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Read NR library state"); }
        finally { _scanningNr = false; }
    }

    private async Task UpdateStatusInfoAsync()
    {
        string? directory = _host?.AddonsPath;
        AddonDllStatus status = await RunDiskAsync(() => AddonDllChecker.Check(directory, [DlssDllRequirements.Dlss5Tag]));

        if (status.Severity == 2)
        {
            TextBlock_RuntimeOk.Visibility = Visibility.Collapsed;
            ShowInfo("DLSS5 插件缺必需文件",
                status.Summary + " —— DLSS5 那几个插件现在起不来。点右上角「安装必要组件」可以一次装上。",
                InfoBarSeverity.Error);
        }
        else if (status.Severity == 1)
        {
            TextBlock_RuntimeOk.Visibility = Visibility.Collapsed;
            ShowInfo("Streamline 不全",
                status.Summary + " —— 插件能加载，但可能不出画面。",
                InfoBarSeverity.Warning);
        }
        else
        {
            // 齐了：标题右侧的常驻绿字（不再用占一整行的 InfoBar）
            InfoBar_Status.IsOpen = false;
            TextBlock_RuntimeOk.Visibility = Visibility.Visible;
        }
    }

    #endregion

    #region 安装

    private async void Button_InstallDll_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking || sender is not FrameworkElement { DataContext: DllFamilyViewModel family }
            || _host is not { } host || family.SelectedComponent is not { } component) return;
        _isWorking = true;
        ProgressBar_Overall.IsIndeterminate = true;
        ProgressBar_Overall.Visibility = Visibility.Visible;
        TextBlock_Status.Text = $"正在校验并切换 {component.Version}，同步游戏插件包…";
        bool needsDownload = false;
        try
        {
            needsDownload = await RunDiskAsync(() =>
            {
                var store = new DllVersionStore(AppConfig.CacheRoot);
                if (!store.Has(component.Family, component.Version)) return true;
                foreach (string source in Directory.EnumerateFiles(store.DirectoryFor(component.Family, component.Version), "*.dll"))
                {
                    DllFingerprintCatalog.ValidateKnownFile(component.Family, component.Version, source);
                    HysxFileLink.LinkOrCopy(source, Path.Combine(host.AddonsPath, Path.GetFileName(source)));
                }
                AppConfig.SetInstalledDllVariant(component.Family, component.Version);
                AppConfig.SetInstalledDllVariantSize(component.Family,
                    DllInstaller.GetInstalledSize(DllInstaller.Scan(host.AddonsPath), family.Family));
                GameAddonPackService.SyncAll();
                return false;
            });
            if (!needsDownload)
            {
                await LoadInstalledAsync();
                await BuildFamiliesAsync();
                await UpdateStatusInfoAsync();
                TextBlock_Status.Text = $"已切换到 {component.Version}（重启游戏生效）";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInfo("DLL 切换失败", ex.Message, InfoBarSeverity.Error); }
        finally { _isWorking = false; }
        if (needsDownload && IsOperationCurrent) await RunInstallAsync(component);
    }

    private async void Toggle_NrGlobal_Toggled(object sender, RoutedEventArgs e)
    {
        if (_buildingCards || sender is not ToggleSwitch { DataContext: NrVersionCardViewModel row } toggle) return;
        bool expected = row.IsAvailable;
        bool available = toggle.IsOn;
        if (available == expected) return;
        if (_isWorking) { toggle.IsOn = expected; return; }
        _isWorking = true;
        TextBlock_Status.Text = "正在保存 NR 可选状态…";
        try
        {
            await RunDiskAsync(() =>
            {
                AppConfig.SetNrVersionEnabled(row.Version, available);
                if (AppConfig.IsNrVersionEnabled(row.Version) != available)
                    throw new IOException("NR 可选状态未能保存。");
            });
            // 仅替换受影响的一张卡片，无需再扫描 DLL 或刷新所有游戏。
            int index = NrVersions.IndexOf(row);
            _buildingCards = true;
            try
            {
                if (index >= 0) NrVersions[index] = new NrVersionCardViewModel(row.Version, row.Component,
                    row.IsInstalled, available, _nrUpdates.Contains(row.Version));
            }
            finally { _buildingCards = false; }
            TextBlock_Status.Text = "NR 可选状态已保存；已有游戏选择保留。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsOperationCurrent) toggle.IsOn = expected;
            ShowInfo("NR 可选状态保存失败", ex.Message, InfoBarSeverity.Error);
        }
        finally { _isWorking = false; }
    }
    private void Button_OpenNr_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NrVersionCardViewModel row })
            Process.Start(new ProcessStartInfo("explorer.exe", new DllVersionStore(AppConfig.CacheRoot).DirectoryFor("dlssnr", row.Version)) { UseShellExecute = true });
    }

    private async void Button_DownloadNr_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking || sender is not FrameworkElement { DataContext: NrVersionCardViewModel { Component: { } component } }) return;
        _isWorking = true;
        ProgressBar_Overall.Visibility = Visibility.Visible;
        ProgressBar_Overall.IsIndeterminate = false;
        TextBlock_Status.Text = $"正在准备下载 {component.Version}…";
        long revision = _operationRevision;
        long generation = _operationGeneration;
        long progressRevision = ++_progressRevision;
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (!_isWorking || revision != _operationRevision || progressRevision != _progressRevision
                    || !IsPageGenerationCurrent(generation)) return;
                ProgressBar_Overall.Value = p.Percent ?? 0;
                TextBlock_Status.Text = $"正在下载 {component.Version}… {p.BytesReceived / 1024d / 1024d:F1} MB";
            });
            await RunDiskTaskAsync(() => NrRuntimeLibrary.DownloadVersionAsync(AppConfig.CacheRoot, component, progress));
            _progressRevision++;
            _nrUpdates.Remove(component.Version);
            await RefreshNrCardsAsync();
            ShowInfo("NR 已加入版本库", component.Version + "；可在 OPT 或游戏插件卡片内选择。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInfo("NR 下载失败", ex.Message, InfoBarSeverity.Error); }
        finally { _isWorking = false; }
    }

    private async void Button_DeleteNr_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking || sender is not FrameworkElement { DataContext: NrVersionCardViewModel row }) return;
        _isWorking = true;
        ProgressBar_Overall.Visibility = Visibility.Visible;
        ProgressBar_Overall.IsIndeterminate = true;
        TextBlock_Status.Text = $"正在删除 NR 归档 {row.Version}…";
        try
        {
            await RunDiskAsync(() =>
            {
                var store = new DllVersionStore(AppConfig.CacheRoot);
                bool existed = store.Has("dlssnr", row.Version);
                if (existed && !store.DeleteVersion("dlssnr", row.Version))
                    throw new IOException("NR 版本删除失败，请检查文件占用和目录权限。");
            });
            _nrUpdates.Remove(row.Version);
            await RefreshNrCardsAsync();
            ShowInfo("NR 版本已删除", row.Version + "；游戏当前使用的硬链接与默认运行库保留。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete NR version {Version}", row.Version);
            ShowInfo("NR 删除失败", ex.Message, InfoBarSeverity.Error);
        }
        finally { _isWorking = false; }
    }

    private async Task CheckNrUpdatesAsync(string? version = null)
    {
        if (_isWorking) return;
        _isWorking = true;
        TextBlock_Status.Text = "正在检查 NR 更新并校验本地内容…";
        ProgressBar_Overall.Visibility = Visibility.Visible;
        ProgressBar_Overall.IsIndeterminate = true;
        try
        {
            var result = await RunBackgroundAsync(() => RemoteCatalogService.RefreshAsync(force: true));
            if (!result.DllFingerprintsUpdated)
            {
                ShowInfo("NR 更新检查未完成", result.Message + " 保留上次有效目录。", InfoBarSeverity.Warning);
                return;
            }
            _catalog = await RunBackgroundAsync(() => DllComponentCatalog.LoadAsync());
            var store = new DllVersionStore(AppConfig.CacheRoot);
            var updates = await RunDiskAsync(() => DllFingerprintCatalog.Fingerprints
                .Where(f => f.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f.Version, StringComparer.OrdinalIgnoreCase)
                .Where(group =>
                {
                    string file = store.FilePath("dlssnr", group.Key, NrRuntimeLibrary.FileName);
                    if (file.Length == 0) return false;
                    try { DllFingerprintCatalog.ValidateKnownFile("dlssnr", group.Key, file); return false; }
                    catch { return true; }
                }).Select(group => group.Key).ToArray());
            _nrUpdates.Clear();
            foreach (string update in updates) _nrUpdates.Add(update);
            await BuildFamiliesAsync();
            ShowInfo("NR 更新检查完成", version is not null
                ? _nrUpdates.Contains(version) ? version + " 有更新，可点击更新下载。" : version + " 未发现内容更新；新版本已列在版本库。"
                : $"发现 {_nrUpdates.Count} 个本地版本内容更新；所有可下载版本已刷新。", InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInfo("检查更新失败", ex.Message, InfoBarSeverity.Warning); }
        finally { _isWorking = false; }
    }

    private void Button_OpenDllVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstalledDllVersionRow row })
            Process.Start(new ProcessStartInfo("explorer.exe", new DllVersionStore(AppConfig.CacheRoot).DirectoryFor(row.FamilyId, row.Version)) { UseShellExecute = true });
    }

    private async void Button_ConsolidateNr_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking || _host is not { } host) return;
        _isWorking = true;
        TextBlock_Status.Text = "正在校验 NR 内容并整理硬链接…";
        ProgressBar_Overall.Visibility = Visibility.Visible;
        ProgressBar_Overall.IsIndeterminate = true;
        try
        {
            var errors = await RunDiskAsync(() => NrRuntimeLibrary.Consolidate(AppConfig.CacheRoot,
                host.AddonsPath, AppConfig.OptiScalerRootPath, AppConfig.CacheRoot));
            ShowInfo(errors.Count == 0 ? "NR 硬链接整理完成" : "部分 NR 未能整理",
                errors.Count == 0 ? "受管目录中相同内容的 NR 已共用硬链接。" : string.Join(Environment.NewLine, errors),
                errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInfo("NR 硬链接整理失败", ex.Message, InfoBarSeverity.Error); }
        finally { _isWorking = false; }
    }

    private async void Button_ImportNr_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking)
        {
            ShowInfo("正在处理 DLL 任务", "请等待当前刷新或下载完成后再导入 NR。", InfoBarSeverity.Informational);
            return;
        }
        _isWorking = true;
        string temporary = Path.Combine(AppConfig.CacheRoot, "nr-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            _logger.LogInformation("Opening NR import picker");
            string? file = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("NR DLL", ".dll"), ("ZIP", ".zip"));
            EnsureOperationCurrent();
            if (file is null) return;
            TextBlock_Status.Text = "正在校验并归档 NR…";
            ProgressBar_Overall.Visibility = Visibility.Visible;
            ProgressBar_Overall.IsIndeterminate = true;
            string version = await RunDiskAsync(() =>
            {
                Directory.CreateDirectory(temporary);
                string source = Path.Combine(temporary, NrRuntimeLibrary.FileName);
                if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(file);
                    var entries = zip.Entries.Where(x => Path.GetFileName(x.FullName).Equals(NrRuntimeLibrary.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (entries.Length != 1) throw new IOException("ZIP 必须包含唯一的 nvngx_dlssnr.dll。");
                    entries[0].ExtractToFile(source);
                }
                else File.Copy(file, source);
                string hash = NrRuntimeLibrary.Hash(source);
                var fingerprint = DllFingerprintCatalog.Find("dlssnr", new FileInfo(source).Length, hash);
                if (!DllFingerprintCatalog.ValidatePe(source, fingerprint, out string peError))
                    throw new IOException("NR PE 验证失败：" + peError);
                string label = fingerprint?.Version ?? ("自定义-" + hash[..16]);
                var store = new DllVersionStore(AppConfig.CacheRoot);
                store.Archive("dlssnr", label, [source]);
                string stored = store.FilePath("dlssnr", label, NrRuntimeLibrary.FileName);
                if (stored.Length == 0 || NrRuntimeLibrary.Hash(stored) != hash) throw new IOException("NR 导入校验失败。");
                return label;
            });
            await RefreshNrCardsAsync();
            ShowInfo("NR 已加入版本库", version + "；可在游戏插件或 OptiScaler 卡片中选择。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NR import failed");
            ShowInfo("导入失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            try { await NrRuntimeWorkQueue.RunAsync(() => HysxUtil.TryDeleteDirectory(temporary)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Clean NR import staging"); }
            _isWorking = false;
        }
    }

    /// <summary>「安装必要组件」：缺 nvngx_dlssnr.dll 就装默认那版，缺 Streamline 就装最新那版</summary>
    private async void Button_FixDlss5_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking || _host is not { } host || _catalog is not { } catalog) return;
        _isWorking = true;
        TextBlock_Status.Text = "正在检查必要组件…";
        ProgressBar_Overall.Visibility = Visibility.Visible;
        ProgressBar_Overall.IsIndeterminate = true;
        try
        {
            var toInstall = new List<DllComponent>();
            AddonDllStatus status = await RunDiskAsync(() => AddonDllChecker.Check(host.AddonsPath, [DlssDllRequirements.Dlss5Tag]));
            if (status.MissingRequired.Any(r => r.Files.Contains(NrRuntimeLibrary.FileName, StringComparer.OrdinalIgnoreCase))
                && DllComponentCatalog.FamilyOf("dlssnr") is { } nrFamily
                && DllSelectionPolicy.SelectDefault(nrFamily, catalog.Of("dlssnr"), OptiScalerPresetCatalog.GpuClass) is { } nr)
                toInstall.Add(nr);
            // 2.14.1.0 已无 sl.interposer.dll，保留原来的兼容版本选择。
            DllFamily? slFamily = DllComponentCatalog.FamilyOf("streamline");
            var slVersions = catalog.Of("streamline");
            bool slMissing = status.MissingRequired.Concat(status.MissingRecommended)
                .Any(r => r.Files.Contains("sl.interposer.dll", StringComparer.OrdinalIgnoreCase));
            if (slMissing && slVersions.Count > 0)
                toInstall.Add(slVersions.FirstOrDefault(c => string.Equals(c.Version, slFamily?.PreferredVersion,
                    StringComparison.OrdinalIgnoreCase)) ?? slVersions[0]);
            if (toInstall.Count == 0)
            {
                TextBlock_Status.Text = "已经齐了，不用补。";
                return;
            }
            foreach (var component in toInstall)
            {
                await RunInstallAsync(component, quiet: true, ownsWork: false);
                EnsureOperationCurrent();
            }
            // 批量安装只在最后扫描/识别/重建一次，不重新联网刷新目录。
            await LoadInstalledAsync();
            await BuildFamiliesAsync();
            await UpdateStatusInfoAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInfo("安装必要组件失败", ex.Message, InfoBarSeverity.Error); }
        finally { _isWorking = false; }
    }

    private async Task RunInstallAsync(DllComponent component, bool quiet = false, bool ownsWork = true)
    {
        if (_host is not { } host || (ownsWork && _isWorking)) return;
        if (ownsWork) _isWorking = true;
        ProgressBar_Overall.IsIndeterminate = false;
        ProgressBar_Overall.Value = 0;
        ProgressBar_Overall.Visibility = Visibility.Visible;
        long progressRevision = ++_progressRevision;
        try
        {
            DllFamily? family = DllComponentCatalog.FamilyOf(component.Family);
            TextBlock_Status.Text = $"正在下载 {family?.DisplayName ?? component.Family} {component.Version}…";
            long revision = _operationRevision;
            long generation = _operationGeneration;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (!_isWorking || revision != _operationRevision || progressRevision != _progressRevision
                    || !IsPageGenerationCurrent(generation)) return;
                ProgressBar_Overall.Value = p.Percent ?? 0;
                TextBlock_Status.Text = $"正在下载 {component.Version}… {p.BytesReceived / 1024d / 1024d:F1} MB";
            });
            // 包括首次 await 前的文件访问、解压续接、归档、内容整合及 SyncAll；一次事务在 worker 完成。
            DllInstallResult result = await RunDiskTaskAsync(async () =>
            {
                var installed = await DllInstaller.InstallAsync(host.AddonsPath, component, progress).ConfigureAwait(false);
                if (!installed.Ok) return installed;
                AppConfig.SetInstalledDllVariant(component.Family, component.Version);
                if (family is not null)
                    AppConfig.SetInstalledDllVariantSize(family.Id,
                        DllInstaller.GetInstalledSize(DllInstaller.Scan(host.AddonsPath), family));
                var store = new DllVersionStore(AppConfig.CacheRoot);
                int archived = store.Archive(component.Family, component.Version,
                    installed.Installed.Select(name => Path.Combine(host.AddonsPath, name)));
                _logger.LogInformation("Runtime dll archived: {Family} {Version} -> {Count} files",
                    component.Family, component.Version, archived);
                // NR 不允许把归档硬链接失败当作成功；保留内容与实际链接身份校验。
                if (component.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))
                {
                    string source = Path.Combine(host.AddonsPath, NrRuntimeLibrary.FileName);
                    string target = store.FilePath("dlssnr", component.Version, NrRuntimeLibrary.FileName);
                    if (target.Length == 0 || !HysxFileLink.AreHardLinked(source, target))
                        throw new IOException("NR 归档硬链接校验失败，已停止同步游戏插件包。");
                    DllFingerprintCatalog.ValidateKnownFile("dlssnr", component.Version, target);
                }
                var linkErrors = NrRuntimeLibrary.Consolidate(AppConfig.CacheRoot,
                    host.AddonsPath, AppConfig.OptiScalerRootPath, AppConfig.CacheRoot);
                if (linkErrors.Count > 0) throw new IOException(string.Join(Environment.NewLine, linkErrors));
                GameAddonPackService.SyncAll();
                return installed;
            });
            _progressRevision++;
            if (result.Ok)
            {
                _logger.LogInformation("DLL installed: {Family} {Version} -> {Files}", component.Family,
                    component.Version, string.Join(",", result.Installed));
                string extra = result.Overwritten.Count > 0 ? $"（覆盖了 {string.Join("、", result.Overwritten)}）" : string.Empty;
                TextBlock_Status.Text = $"{family?.DisplayName ?? component.Family} {component.Version} 装好了：{string.Join("、", result.Installed)}{extra}";
                if (!quiet) ShowInfo("装好了", TextBlock_Status.Text, InfoBarSeverity.Success);
            }
            else
            {
                _logger.LogWarning("DLL install failed: {Error}", result.Error);
                TextBlock_Status.Text = "安装失败：" + result.Error;
                ShowInfo("安装失败", result.Error ?? string.Empty, InfoBarSeverity.Error);
            }
            if (!quiet)
            {
                await LoadInstalledAsync();
                await BuildFamiliesAsync();
                await UpdateStatusInfoAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install DLL");
            ShowInfo("安装失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _progressRevision++;
            if (ownsWork) _isWorking = false;
        }
    }

    /// <summary>
    /// 需求6：删掉归档里的一个 dll 版本。
    /// 只删 <c>&lt;CacheRoot&gt;\dlls\&lt;family&gt;\&lt;version&gt;\</c>，共享 Addons 目录里
    /// 当前生效的那一份不动（否则 ReShade 直接少文件）。
    /// </summary>
    private async void Button_DeleteDllVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking || sender is not FrameworkElement { DataContext: InstalledDllVersionRow row })
        {
            return;
        }

        _isWorking = true;
        TextBlock_Status.Text = $"正在删除 DLL 归档 {row.Version}…";
        try
        {
            if (await RunDiskAsync(() => new DllVersionStore(AppConfig.CacheRoot).DeleteVersion(row.FamilyId, row.Version)))
            {
                _logger.LogInformation("Archived runtime dll deleted: {Family} {Version}", row.FamilyId, row.Version);
                TextBlock_Status.Text = $"已删掉归档里的 {row.FamilyId} {row.Version}（共享目录里现在用的那份没动）。";
                await BuildFamiliesAsync();
            }
            else
            {
                TextBlock_Status.Text = $"归档里没有 {row.FamilyId} {row.Version}，可能已经被删了。";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete archived dll version {Family} {Version}", row.FamilyId, row.Version);
            if (IsOperationCurrent) TextBlock_Status.Text = "删除失败：" + ex.Message;
        }
        finally { _isWorking = false; }
    }

    #endregion

    #region 顶部按钮

    private async void Button_Refresh_Click(object sender, RoutedEventArgs e)
    {
        await CheckNrUpdatesAsync();
    }

    private async void Button_PickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking) return;
        _isWorking = true;
        bool changed = false;
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(XamlRoot);
            EnsureOperationCurrent();
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (await RunDiskAsync(() => ShadeHostLocator.FromShadeRoot(folder)) is null)
            {
                ShowInfo("这个目录不是 HoYoShade",
                    $"在 {folder} 里找不到 ReShade64.dll。请选到 HoYoShade 这一层。",
                    InfoBarSeverity.Error);
                return;
            }

            PluginHostLocator.ManualShadeRoot = folder;
            changed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pick shade folder");
            if (IsOperationCurrent) TextBlock_Status.Text = "选择目录失败：" + ex.Message;
        }
        finally { _isWorking = false; }
        if (changed && IsOperationCurrent) await RefreshAsync();
    }

    private void Button_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = _host?.AddonsPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open addons folder");
        }
    }

    #endregion

    #region InfoBar

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        if (!IsLoaded || (_isWorking && !IsOperationCurrent)) return;
        InfoBar_Status.Title = title;
        InfoBar_Status.Message = message;
        InfoBar_Status.Severity = severity;
        InfoBar_Status.IsOpen = true;
    }

    #endregion
}

/// <summary>左边列表里的一个 dll</summary>
public sealed class InstalledDllViewModel
{
    public InstalledDllViewModel(InstalledDll dll)
    {
        Dll = dll;
        FileName = dll.FileName;
        SizeText = dll.SizeText;
        VersionText = string.IsNullOrWhiteSpace(dll.Version)
            ? "版本未知"
            : "版本 " + DllVersion.Normalize(dll.Version);
    }

    public InstalledDll Dll { get; }

    public string FileName { get; }

    public string SizeText { get; }

    public string VersionText { get; }
}

/// <summary>右边一类组件</summary>
public partial class DllFamilyViewModel : ObservableObject
{
    private readonly IReadOnlyList<DllComponent> _components;

    public DllFamilyViewModel(
        DllFamily family,
        IReadOnlyList<DllComponent> components,
        IReadOnlyList<InstalledDll> installed,
        DllVariantProbe probe)
    {
        Family = family;
        _components = components;
        Title = family.DisplayName;
        Files = installed.Where(d => DllInstaller.Matches(d.FileName, family)).Select(d => new InstalledDllViewModel(d)).ToArray();

        string requirement = family.Level == DllRequirementLevel.Required ? "缺了插件起不来" : "缺了可能不出画面";
        Note = $"{family.FilePattern} · {requirement} —— {family.Note}";
        RequiredBadgeVisibility = family.Level == DllRequirementLevel.Required ? Visibility.Visible : Visibility.Collapsed;

        // 版本列表：带上备注（RTX40 / SF / Lecram 这种）。Lecram 固定在顶部（用户要求）。
        Versions = [.. components.Select(DisplayOf)
            .OrderByDescending(v => v.Contains("Lecram", StringComparison.OrdinalIgnoreCase))];

        // 身份判定由后台快照提供；UI 模型构造不得再同步求 SHA256。

        CurrentVersion = probe.Variant ?? DllInstaller.GetInstalledVersion(installed, family);

        bool hasAny = InstalledDlls(installed, family).Any();
        HasInstalled = hasAny;
        IsCurrentKnown = probe.Known;
        InstalledText = !hasAny
            ? "还没装"
            : probe.Known
                ? $"已装：{probe.Display}"
                : string.IsNullOrWhiteSpace(probe.Display)
                    ? "已装（读不出版本）"
                    : $"已装：PE {probe.Display}（变体未确认）";

        // 只有当前文件能和清单准确对上时才自动选中；未知 NR 保持空选，避免回退到第一条 50xx。
        DllComponent? match = components.FirstOrDefault(c => string.Equals(c.Version, probe.Variant, StringComparison.OrdinalIgnoreCase))
                              ?? (family.Id.Equals("dlssnr", StringComparison.OrdinalIgnoreCase) ? null
                                  : components.FirstOrDefault(c => DllVersion.IsSame(c.Version, CurrentVersion)));
        if (match is not null)
        {
            SelectedVersion = DisplayOf(match);
        }
        else if (!hasAny && !family.Id.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))
        {
            SelectedVersion = DisplayOf(components.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Note)) ?? components[0]);
        }
        else
        {
            SelectedVersion = string.Empty;
        }
    }

    public DllFamily Family { get; }
    public IReadOnlyList<InstalledDllViewModel> Files { get; }

    public string Title { get; }

    public string Note { get; }

    public string InstalledText { get; }

    public bool HasInstalled { get; }

    public bool IsCurrentKnown { get; }

    /// <summary>盘上现在这一版（PE 版本 + 记账变体），给「使用中」角标用</summary>
    public string? CurrentVersion { get; }

    // ===================== 需求6：这一类 dll 的已安装版本（每行一个删除） =====================

    public ObservableCollection<InstalledDllVersionRow> InstalledVersionRows { get; } = [];

    public void SetInstalledVersions(IEnumerable<InstalledDllVersionRow> rows)
    {
        InstalledVersionRows.Clear();
        foreach (InstalledDllVersionRow row in rows)
        {
            InstalledVersionRows.Add(row);
        }

        OnPropertyChanged(nameof(InstalledVersionsSummary));
    }

    public string InstalledVersionsSummary => InstalledVersionRows.Count == 0
        ? "已安装版本：（还没有归档；装 / 重装一次就会留档）"
        : $"已安装版本：{InstalledVersionRows.Count} 个";

    public Visibility RequiredBadgeVisibility { get; }

    public ObservableCollection<string> Versions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedComponent))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private string selectedVersion = string.Empty;

    public DllComponent? SelectedComponent => _components.FirstOrDefault(c => DisplayOf(c) == SelectedVersion);

    public bool CanInstall => SelectedComponent is not null;

    public void SelectComponent(DllComponent component) => SelectedVersion = DisplayOf(component);

    private static string DisplayOf(DllComponent component) =>
        string.IsNullOrWhiteSpace(component.Note) ? component.Version : $"{component.Version}（{component.Note}）";

    private static IEnumerable<InstalledDll> InstalledDlls(IEnumerable<InstalledDll> installed, DllFamily family) =>
        installed.Where(d => DllInstaller.Matches(d.FileName, family));
}

/// <summary>
/// 「DLL 配置」页「已安装版本」列表里的一行（需求6）：归档里的一份版本 + 删除。
/// 数据模板里只能挂 Click，所以删除动作由页面处理器读这一行来做。
/// </summary>
public sealed class NrVersionCardViewModel(string version, DllComponent? component, bool installed, bool available, bool update, bool showDownloadHeading = false)
{
    public string Version { get; } = version;
    public DllComponent? Component { get; } = component;
    public bool IsInstalled { get; } = installed;
    public bool IsAvailable { get; } = available;
    public string StatusText => update ? "有更新" : IsInstalled ? IsAvailable ? "已下载 · 可供选择" : "已下载 · 已从可选列表关闭" : "未下载";
    public string DownloadText => update ? "更新" : "下载";
    public Visibility DownloadVisibility => Component is not null && (!IsInstalled || update) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InstalledActionsVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownloadHeadingVisibility => showDownloadHeading ? Visibility.Visible : Visibility.Collapsed;
    public Microsoft.UI.Xaml.Media.Brush BarBrush => CatalogRowVisual.Bar(IsInstalled && IsAvailable);
}

public sealed class InstalledDllVersionRow
{
    public InstalledDllVersionRow(string familyId, string version, string? displayText, bool isCurrent, bool archived = true)
    {
        FamilyId = familyId;
        Version = version;
        DisplayText = string.IsNullOrWhiteSpace(displayText) ? version : displayText;
        IsCurrent = isCurrent;
        IsArchived = archived;
    }

    public Microsoft.UI.Xaml.Media.Brush BarBrush => CatalogRowVisual.Bar(IsCurrent);

    public bool IsArchived { get; }

    public string FamilyId { get; }

    public string Version { get; }

    public string DisplayText { get; }

    public bool IsCurrent { get; }

    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}
