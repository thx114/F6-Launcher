using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Extensions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

internal static class NrVersionPicker
{
    private sealed class FillState
    {
        public readonly DeferredPickerUpdate<Snapshot> Updates = new();
        public Task<List<string>>? Pending;
        public bool Attached;
        public bool DispatchQueued;
    }
    private sealed record Snapshot(string? Selected, IReadOnlyList<string> Versions);
    private static readonly ILogger Logger = AppConfig.GetLogger<PickerLogToken>();
    private sealed class PickerLogToken { }
    private static readonly ConditionalWeakTable<ComboBox, FillState> FillStates = new();
    private static FillState For(ComboBox combo) => FillStates.GetValue(combo, static _ => new FillState());

    public static void RefreshInline(DependencyObject root, string? selected)
    {
        if (root is ComboBox { Name: "ComboBox_NrVersion" } combo)
        {
            Fill(combo, selected);
            return;
        }
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            RefreshInline(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), selected);
    }

    // 保留同步调用形状供 Loaded/DropDownOpened 使用；磁盘枚举异步完成。
    public static async void Fill(ComboBox combo, string? selected)
    {
        var state = For(combo);
        Attach(combo, state);
        long revision = state.Updates.Begin();
        var pending = state.Pending ??= NrRuntimeWorkQueue.RunAsync(ReadVersions);
        try
        {
            var versions = await pending;
            if (!combo.IsLoaded || combo.XamlRoot is null) return;
            if (state.Updates.Complete(revision, new(selected, versions))) QueueApply(combo, state);
        }
        catch (Exception ex)
        {
            // async void 事件适配器不能把磁盘/已卸载控件异常传播到 dispatcher。
            Logger.LogWarning(ex, "NR picker version discovery failed");
        }
        finally { if (ReferenceEquals(state.Pending, pending)) state.Pending = null; }
    }

    private static List<string> ReadVersions() => NrRuntimeLibrary.SelectableVersions(
        new DllVersionStore(AppConfig.CacheRoot).ListVersions("dlssnr")
            .Where(v => File.Exists(Path.Combine(v.Directory, NrRuntimeLibrary.FileName)))
            .Select(v => v.Version), AppConfig.IsNrVersionEnabled).ToList();

    // Opt's page already owns a background version snapshot; share only the safe UI binding.
    public static void SetVersions(ComboBox combo, string? selected, IReadOnlyList<string> versions)
    {
        var state = For(combo);
        Attach(combo, state);
        long revision = state.Updates.Begin();
        state.Updates.Complete(revision, new(selected, versions.ToArray()));
        QueueApply(combo, state);
    }

    private static void Attach(ComboBox combo, FillState state)
    {
        if (state.Attached) return;
        state.Attached = true;
        combo.SelectionChanged += InvalidateFill;
        combo.Unloaded += InvalidateFillOnUnload;
        combo.DropDownClosed += OnDropDownClosed;
    }

    private static void QueueApply(ComboBox combo, FillState state)
    {
        if (state.DispatchQueued) return;
        state.DispatchQueued = true;
        if (!combo.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            state.DispatchQueued = false;
            try
            {
                if (!combo.IsLoaded || combo.XamlRoot is null) { state.Updates.Invalidate(); return; }
                // DropDownOpened can finish its disk read while WinUI is handling an item click.
                // Never clear/replace that popup's containers; Closed schedules another turn.
                if (state.Updates.TryTake(combo.IsDropDownOpen, out var snapshot))
                    ApplyVersions(combo, snapshot!.Selected, snapshot.Versions);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "NR picker snapshot binding failed"); }
        })) state.DispatchQueued = false;
    }

    private static void OnDropDownClosed(object sender, object e)
    {
        if (sender is ComboBox combo) QueueApply(combo, For(combo));
    }

    /// <summary>Exit WinUI's item-selection callback before changing availability or starting IO.</summary>
    public static async Task CloseForSelectionAsync(ComboBox combo, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (!combo.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (completion.Task.IsCompleted) return;
            try
            {
                if (!combo.IsLoaded || combo.XamlRoot is null) { completion.TrySetCanceled(); return; }
                combo.IsDropDownOpen = false;
                completion.TrySetResult(true);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetCanceled();
        await completion.Task;
    }

    private static void ApplyVersions(ComboBox combo, string? selected, IReadOnlyList<string> versions)
    {
        var items = new List<ComboBoxItem> { new() { Content = "NR：默认" } };
        foreach (var version in versions) items.Add(new ComboBoxItem { Content = "NR：" + version, Tag = version });
        int index = selected is null ? 0 : items.FindIndex(item => string.Equals(item.Tag as string, selected, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            items.Add(new ComboBoxItem { Content = "NR：" + selected + "（不可选）", Tag = selected, IsEnabled = false });
            index = items.Count - 1;
        }
        bool sameItems = combo.Items.Count == items.Count && combo.Items.OfType<ComboBoxItem>().Zip(items)
            .All(pair => Equals(pair.First.Content, pair.Second.Content)
                && Equals(pair.First.Tag, pair.Second.Tag) && pair.First.IsEnabled == pair.Second.IsEnabled);
        if (sameItems && combo.SelectedIndex == index) return;
        combo.Tag = true;
        try
        {
            if (!sameItems) combo.ItemsSource = items;
            combo.SelectedIndex = index;
        }
        finally { combo.Tag = false; }
    }

    private static void InvalidateFill(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: false } combo) For(combo).Updates.Invalidate();
    }

    private static void InvalidateFillOnUnload(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo) For(combo).Updates.Invalidate();
    }

    public static async Task<(bool Apply, string? Version)> ShowAsync(XamlRoot root, string? selected, string? currentPath)
    {
        // 先显示可见的读取状态；不要在 ShowAsync 的首个 await 前做 SHA256/磁盘扫描。
        var versions = new List<string>();
        var combo = new ComboBox { MinWidth = 280, IsEnabled = false };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "仅对当前游戏生效；重启游戏后加载所选 NR。", TextWrapping = TextWrapping.Wrap });
        var currentText = new TextBlock { Text = "正在读取 NR 版本并校验当前内容…", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(currentText);
        panel.Children.Add(combo);
        var warning = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        panel.Children.Add(warning);
        var dialog = new ContentDialog { XamlRoot = root, Title = "选择这个游戏的 NR 版本", Content = panel,
            PrimaryButtonText = "应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false };
        combo.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = combo.IsEnabled && combo.SelectedIndex >= 0;
        using var lifetime = new CancellationTokenSource();
        var cancellationToken = lifetime.Token;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var snapshot = await NrRuntimeWorkQueue.RunAsync(() =>
                {
                    RemoteCatalogService.ApplyDllFingerprints();
                    var available = ReadVersions();
                    string text = "当前文件：未找到 NR";
                    if (currentPath is not null && File.Exists(currentPath))
                    {
                        var family = DllComponentCatalog.FamilyOf("dlssnr");
                        var installed = DllInstaller.Scan(Path.GetDirectoryName(currentPath))
                            .FirstOrDefault(d => d.FileName.Equals(NrRuntimeLibrary.FileName, StringComparison.OrdinalIgnoreCase));
                        if (family is not null && installed is not null)
                        {
                            var probe = DllVariantResolver.Identify(new DllVersionStore(AppConfig.CacheRoot), family, installed, null, 0);
                            text = "当前文件：" + probe.Display + (probe.Known ? "" : "（变体未确认）");
                        }
                    }
                    return (Versions: available, Text: text);
                }, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;
                versions = snapshot.Versions;
                var labels = new List<string> { "默认（插件 / 构建自带）" };
                labels.AddRange(versions);
                combo.ItemsSource = labels;
                combo.IsEnabled = true;
                combo.SelectedIndex = selected is null ? 0 : versions.FindIndex(v => v.Equals(selected, StringComparison.OrdinalIgnoreCase)) is int index && index >= 0 ? index + 1 : -1;
                currentText.Text = snapshot.Text;
                dialog.IsPrimaryButtonEnabled = combo.SelectedIndex >= 0;
                if (selected is not null && !versions.Contains(selected, StringComparer.OrdinalIgnoreCase))
                {
                    warning.Text = "当前选择：" + selected + "（已从可选列表关闭或归档缺失）。现有选择保留；可启用此版本，或在此改选其他版本。";
                    warning.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested) currentText.Text = "NR 列表或内容校验失败：" + ex.Message;
            }
        };
        dialog.Closed += (_, _) => lifetime.Cancel();
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return (false, null);
            return (true, combo.SelectedIndex > 0 ? versions[combo.SelectedIndex - 1] : null);
        }
        finally { lifetime.Cancel(); }
    }
}
