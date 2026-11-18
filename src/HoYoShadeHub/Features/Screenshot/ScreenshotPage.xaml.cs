using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using HoYoShadeHub.Core;
using HoYoShadeHub.Controls;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.HoYoPlay;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using HoYoShadeHub.Features.ViewHost;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;


namespace HoYoShadeHub.Features.Screenshot;

public sealed partial class ScreenshotPage : PageBase
{


    private readonly ILogger<ScreenshotPage> _logger = AppConfig.GetLogger<ScreenshotPage>();


    private readonly HoYoPlayService _hoyoplayService = AppConfig.GetService<HoYoPlayService>();




    public ScreenshotPage()
    {
        this.InitializeComponent();

        // 截图网格错峰入场；GridView 虚拟化会复用容器，只播首屏已实现容器的，
        // 滚动进来的新容器没有动画（也不会反复淡入 —— Composition 错峰和平台 Entrance 的区别）
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => MotionAnimations.PlayListEntrance(GridView_Images));
    }


    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Image_Emoji.Source = CurrentGameBiz.ToGame().Value switch
        {
            GameBiz.hk4e => new BitmapImage(AppConfig.EmojiPaimon),
            GameBiz.hkrpg => new BitmapImage(AppConfig.EmojiPom),
            GameBiz.bh3 => new BitmapImage(AppConfig.EmojiAI),
            GameBiz.nap => new BitmapImage(AppConfig.EmojiBangboo),
            _ => null,
        };
    }



    protected override async void OnLoaded()
    {
        await InitializeAsync();
    }



    protected override void OnUnloaded()
    {
        _screenshotLoad?.Cancel();
        _screenshotLoad?.Dispose();
        _screenshotLoad = null;
        ++_screenshotRevision;
        try
        {
            foreach (var item in _watchers)
            {
                item.Dispose();
            }
            _watchers.Clear();
            _folders.Clear();
            _screenshotDict.Clear();
            _screenshotItems = null;
            ScreenshotGroups = null!;
            ScreenshotViewSource.Source = null;
        }
        catch { }
    }



    public bool MutliSelect
    {
        get; set
        {
            field = value;
            GridView_Images.SelectionMode = value ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
            Button_BatchConvert.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            TextBlock_SelectCount.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }


    public string SelectCountText { get; set => SetProperty(ref field, value); }


    private List<FileSystemWatcher> _watchers = new();

    private CancellationTokenSource? _screenshotLoad;
    private int _screenshotRevision;

    private void Thumbnail_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args) =>
        ScreenshotThumbnailViewport.UpdateViewport((CachedImage)sender, args);

    private void Thumbnail_Loaded(object sender, RoutedEventArgs args) =>
        ScreenshotThumbnailViewport.Refresh((CachedImage)sender);

    private void Thumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        ScreenshotThumbnailViewport.Refresh((CachedImage)sender);

    private void Thumbnail_Unloaded(object sender, RoutedEventArgs args) =>
        ScreenshotThumbnailViewport.SetVisible((CachedImage)sender, false);

    private List<ScreenshotFolder> _folders = new();

    // FileSystemWatcher 的回调在线程池线程上跑（每个截图目录一个 watcher），会并发碰这个字典 —— 必须线程安全
    private ConcurrentDictionary<string, ScreenshotItem> _screenshotDict = new();

    private ObservableCollection<ScreenshotItem>? _screenshotItems;

    public CollectionViewSource ScreenshotViewSource { get; set => SetProperty(ref field, value); } = new() { IsSourceGrouped = true };

    public ObservableCollection<ScreenshotItemGroup> ScreenshotGroups { get; set { if (SetProperty(ref field, value)) { ScreenshotViewSource.Source = value; } } }


    private bool IsScreenshotLoadCurrent(int revision, CancellationToken token) =>
        !token.IsCancellationRequested && revision == _screenshotRevision && IsLoaded;

    private async Task InitializeAsync()
    {
        if (!IsLoaded) return;
        _screenshotLoad?.Cancel();
        _screenshotLoad?.Dispose();
        _screenshotLoad = CancellationTokenSource.CreateLinkedTokenSource(PageCancellationToken);
        CancellationToken token = _screenshotLoad.Token;
        int revision = ++_screenshotRevision;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<FileSystemWatcher> pendingWatchers = new();
        try
        {
            foreach (var item in _watchers)
            {
                item.Dispose();
            }
            _watchers.Clear();
            _folders.Clear();
            _screenshotDict.Clear();
            ScreenshotGroups = null!;

            string? externalFolder = AppConfig.GetExternalScreenshotFolder(CurrentGameBiz);
            string? userDataFolder = AppConfig.UserDataFolder;
            (string? defaultFolder, string? inGameFolder) = await GetGameScreenshotPathAsync(token);
            var snapshot = await Task.Run(() => ScreenshotDirectorySnapshot.Read(
                defaultFolder, inGameFolder, userDataFolder, externalFolder, false, token,
                folder => pendingWatchers.Add(CreateFileSystemWatcher(folder, token, revision, ready.Task))), token);
            if (!IsScreenshotLoadCurrent(revision, token)) return;
            _folders = snapshot.Folders;
            _screenshotDict = snapshot.ByName;
            _screenshotItems = snapshot.Items;
            ScreenshotGroups = snapshot.Groups;
            _watchers = pendingWatchers;
            pendingWatchers = new();
            ready.TrySetResult();
            foreach (var error in snapshot.Errors)
                _logger.LogWarning(error.Error, "Read screenshot directory {Path}", error.Path);
            UpdateSelectCountText();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize");
        }
        finally
        {
            ready.TrySetCanceled();
            foreach (var watcher in pendingWatchers) watcher.Dispose();
        }
    }


    public async Task<(string? BackupFolder, string? ScreenshotFolder)> GetGameScreenshotPathAsync(CancellationToken token = default)
    {
        try
        {
            var gameId = CurrentGameId;
            var gameBiz = CurrentGameBiz;
            string? name = GameLauncherService.GetGameExeName(gameBiz)?.Replace(".exe", "");
            string? relativePath = gameBiz.Game switch
            {
                GameBiz.hk4e => "ScreenShot",
                GameBiz.hkrpg => @"StarRail_Data\ScreenShots",
                GameBiz.bh3 => @"ScreenShot",
                GameBiz.nap => @"ScreenShot",
                _ => null,
            };
            if (name is null || relativePath is null)
            {
                var config = await _hoyoplayService.GetGameConfigAsync(gameId).WaitAsync(token);
                if (config is not null)
                {
                    name ??= config.ExeFileName.Replace(".exe", "");
                    relativePath ??= config.GameScreenshotDir;
                }
            }
            
            token.ThrowIfCancellationRequested();
            string? installPath = GameLauncherService.GetGameInstallPath(gameId);
            return (null, string.IsNullOrWhiteSpace(installPath) || relativePath is null ? null : Path.Join(installPath, relativePath));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game screenshot path");
        }
        return (null, null);
    }


    private FileSystemWatcher CreateFileSystemWatcher(string folder, CancellationToken token, int revision, Task ready)
    {
        var watcher = new FileSystemWatcher(folder);
        watcher.NotifyFilter = NotifyFilters.FileName;
        foreach (var item in ScreenshotHelper.WatcherFilters)
        {
            watcher.Filters.Add(item);
        }
        watcher.Created += (_, e) => FileSystemWatcher_Created(e, token, revision, ready);
        watcher.Deleted += (_, e) => FileSystemWatcher_Deleted(e, token, revision, ready);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
    
    
    private async void FileSystemWatcher_Created(FileSystemEventArgs e, CancellationToken token, int revision, Task ready)
    {
        try
        {
            await ready.WaitAsync(token);
            if (ScreenshotHelper.IsSupportedExtension(e.FullPath) && File.Exists(e.FullPath))
            {
                await ScreenshotHelper.WaitForFileReleaseAsync(e.FullPath, token);
                token.ThrowIfCancellationRequested();
                var item = new ScreenshotItem(e.FullPath);
                string name = Path.GetFileName(e.FullPath);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsScreenshotLoadCurrent(revision, token) || !_screenshotDict.TryAdd(name, item)) return;
                    ScreenshotGroups ??= new();
                    _screenshotItems ??= new();
                    _screenshotItems.Insert(0, item);
                    if (ScreenshotGroups.FirstOrDefault(x => x.Header == item.TimeMonthDay) is ScreenshotItemGroup group)
                    {
                        group.Insert(0, item);
                    }
                    else
                    {
                        var newGroup = new ScreenshotItemGroup(item.TimeMonthDay, [item]);
                        ScreenshotGroups.Insert(0, newGroup);
                    }
                    UpdateSelectCountText();
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || ready.IsCanceled) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File system watcher created event");
        }
    }


    private async void FileSystemWatcher_Deleted(FileSystemEventArgs e, CancellationToken token, int revision, Task ready)
    {
        try
        {
            await ready.WaitAsync(token);
            string name = Path.GetFileName(e.FullPath);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsScreenshotLoadCurrent(revision, token)) return;
                if (_screenshotDict.TryGetValue(name, out ScreenshotItem? item) &&
                    string.Equals(e.FullPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (ScreenshotGroups?.FirstOrDefault(x => x.Header == item.TimeMonthDay) is ScreenshotItemGroup group)
                    {
                        if (group.Contains(item))
                        {
                            _screenshotDict.TryRemove(name, out _);
                            _screenshotItems?.Remove(item);
                            group.Remove(item);
                            if (group.Count == 0)
                            {
                                ScreenshotGroups.Remove(group);
                            }
                            UpdateSelectCountText();
                        }
                    }
                }
            });
        }
        catch { }
    }



    [RelayCommand]
    private void OpenImageBatchConvertWindow()
    {
        try
        {
            var list = GridView_Images.SelectedItems.Cast<ScreenshotItem>().ToList();
            new ImageBatchConvertWindow().Activate(list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open image batch convert window");
        }
    }


    [RelayCommand]
    private async Task ManageScreenshotFolderAsync()
    {
        try
        {
            var dialog = new ScreenshotFolderManageDialog
            {
                Folders = _folders,
                CurrentGameId = this.CurrentGameId,
                XamlRoot = this.XamlRoot,
            };
            await dialog.ShowAsync();
            if (dialog.FolderChanged)
            {
                string folder = string.Join(';', dialog.Folders.Where(x => x.CanRemove).Select(x => x.Folder));
                AppConfig.SetExternalScreenshotFolder(CurrentGameBiz, folder);
                await InitializeAsync();
                UpdateSelectCountText();
            }
        }
        catch { }
    }


    private void GridView_Images_ItemClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (GridView_Images.SelectionMode is ListViewSelectionMode.None)
            {
                if (e.ClickedItem is ScreenshotItem item)
                {
                    _ = new ImageViewWindow2().ShowWindowAsync(this.XamlRoot.ContentIslandEnvironment.AppWindowId, item, _screenshotItems);
                }
            }
        }
        catch { }
    }


    private async void GridView_Images_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        try
        {
            var list = new List<StorageFile>();
            foreach (var dragItem in e.Items)
            {
                if (dragItem is ScreenshotItem item)
                {
                    if (File.Exists(item.FilePath))
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                        list.Add(file);
                    }
                }
            }
            if (list.Count > 0)
            {
                e.Data.RequestedOperation = DataPackageOperation.Copy;
                e.Data.SetStorageItems(list, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Drag image starting");
        }
    }


    private void GridView_Images_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectCountText();
    }


    private void UpdateSelectCountText()
    {
        try
        {
            SelectCountText = $"{GridView_Images.SelectedItems.Count}/{_screenshotItems?.Count ?? 0}";
        }
        catch { }
    }



    private void MenuFlyoutItem_ScreenshotInfo_Loading(FrameworkElement sender, object args)
    {
        try
        {
            if (sender.DataContext is ScreenshotItem item)
            {
                item.UpdatePixelSize();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get image pixel size");
        }
    }


    private void MenuFlyoutItem_Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: ScreenshotItem item })
            {
                _ = new ImageViewWindow2().ShowWindowAsync(this.XamlRoot.ContentIslandEnvironment.AppWindowId, item, _screenshotItems);
            }
        }
        catch { }
    }


    private async void MenuFlyoutItem_CopyFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GridView_Images.SelectionMode is ListViewSelectionMode.Multiple && GridView_Images.SelectedItems.Count > 0)
            {
                var list = new List<StorageFile>();
                foreach (ScreenshotItem item in GridView_Images.SelectedItems.Cast<ScreenshotItem>())
                {
                    if (File.Exists(item.FilePath))
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                        list.Add(file);
                    }
                }
                if (list.Count > 0)
                {
                    ClipboardHelper.SetStorageItems(DataPackageOperation.Copy, list.ToArray());
                    InAppToast.MainWindow?.Success(Lang.ImageViewWindow2_CopiedToClipboard, string.Format(Lang.ScreenshotPage_Total0Files, list.Count), 1500);
                }
            }
            else if (sender is FrameworkElement fe && fe.DataContext is ScreenshotItem item)
            {
                if (File.Exists(item.FilePath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    ClipboardHelper.SetStorageItems(DataPackageOperation.Copy, file);
                    InAppToast.MainWindow?.Success(Lang.ImageViewWindow2_CopiedToClipboard, null, 1500);
                }
                else
                {
                    InAppToast.MainWindow?.Warning(Lang.ImageViewWindow2_FileDoesNotExist, item.FilePath, 3000);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy file to clipboard");
        }
    }


    private async void MenuFlyoutItem_CopyAsJPG_Click(object sender, RoutedEventArgs e)
    {
        static string GetSizeString(long size)
        {
            const double KB = 1 << 10;
            const double MB = 1 << 20;
            if (size >= MB)
            {
                return $"{size / MB:F2} MB";
            }
            else
            {
                return $"{size / KB:F2} KB";
            }
        }
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is ScreenshotItem item)
            {
                if (File.Exists(item.FilePath))
                {
                    string jpgFilePath = await ScreenshotHelper.ConvertToJpgAsync(item.FilePath);
                    var file = await StorageFile.GetFileFromPathAsync(jpgFilePath);
                    ClipboardHelper.SetStorageItems(DataPackageOperation.Copy, file);
                    InAppToast.MainWindow?.Success($"{Lang.ImageViewWindow2_CopiedToClipboard} ({GetSizeString(new FileInfo(jpgFilePath).Length)})", null, 1500);
                }
                else
                {
                    InAppToast.MainWindow?.Warning(Lang.ImageViewWindow2_FileDoesNotExist, item.FilePath, 3000);
                }
            }
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error(Lang.ImageViewWindow2_CopyAsJPG, ex.Message, 3000);
            _logger.LogError(ex, "Failed to copy file as JPG to clipboard");
        }
    }


    private async void MenuFlyoutItem_OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is ScreenshotItem item)
            {
                if (File.Exists(item.FilePath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    var options = new FolderLauncherOptions();
                    options.ItemsToSelect.Add(file);
                    await Launcher.LaunchFolderAsync(await file.GetParentAsync(), options);
                }
                else
                {
                    InAppToast.MainWindow?.Warning(Lang.ImageViewWindow2_FileDoesNotExist, item.FilePath, 3000);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file in explorer");
        }
    }


    private async void MenuFlyoutItem_OpenWithDefault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is ScreenshotItem item)
            {
                if (File.Exists(item.FilePath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    await Launcher.LaunchFileAsync(file);
                }
                else
                {
                    InAppToast.MainWindow?.Warning(Lang.ImageViewWindow2_FileDoesNotExist, item.FilePath, 3000);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file with default application");
        }
    }


    private async void MenuFlyoutItem_OpenWith_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is ScreenshotItem item)
            {
                if (File.Exists(item.FilePath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    var options = new LauncherOptions { DisplayApplicationPicker = true };
                    await Launcher.LaunchFileAsync(file, options);
                }
                else
                {
                    InAppToast.MainWindow?.Warning(Lang.ImageViewWindow2_FileDoesNotExist, item.FilePath, 3000);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file with application picker");
        }
    }


    private async void MenuFlyoutItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GridView_Images.SelectionMode is ListViewSelectionMode.Multiple && GridView_Images.SelectedItems.Count > 0)
            {
                var list = GridView_Images.SelectedItems.Cast<ScreenshotItem>().ToList();
                foreach (ScreenshotItem item in list)
                {
                    if (File.Exists(item.FilePath))
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                        await file.DeleteAsync();
                    }
                    _screenshotItems?.Remove(item);
                    if (ScreenshotGroups?.FirstOrDefault(x => x.Header == item.TimeMonthDay) is ScreenshotItemGroup group)
                    {
                        if (group.Remove(item))
                        {
                            _screenshotDict.TryRemove(item.FileName, out _);
                            if (group.Count == 0)
                            {
                                ScreenshotGroups.Remove(group);
                            }
                        }
                    }
                }
            }
            else if (sender is FrameworkElement fe && fe.DataContext is ScreenshotItem item)
            {
                if (File.Exists(item.FilePath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    await file.DeleteAsync();
                }
                _screenshotItems?.Remove(item);
                if (ScreenshotGroups?.FirstOrDefault(x => x.Header == item.TimeMonthDay) is ScreenshotItemGroup group)
                {
                    if (group.Remove(item))
                    {
                        _screenshotDict.TryRemove(item.FileName, out _);
                        if (group.Count == 0)
                        {
                            ScreenshotGroups.Remove(group);
                        }
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // TODO 使用 RPC 删除
            InAppToast.MainWindow?.Warning(Lang.ImageViewWindow2_UnableToDeleteTheFile, Lang.ImageViewWindow2_InsufficientPermissionsOrTheFileIsInUse, 5000);
            _logger.LogError(ex, "Failed to delete image file");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete image file");
        }
    }


}

// Plain data only: both screenshot layouts share one worker-side enumeration,
// metadata and sorting boundary. No image decoding or XAML objects are created.
internal sealed class ScreenshotDirectorySnapshot
{
    public List<ScreenshotFolder> Folders { get; } = new();
    public ConcurrentDictionary<string, ScreenshotItem> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<ScreenshotItem> Items { get; private set; } = new();
    public ObservableCollection<ScreenshotItemGroup> Groups { get; private set; } = new();
    public List<(string Path, Exception Error)> Errors { get; } = new();

    public static ScreenshotDirectorySnapshot Read(string? backup, string? inGame, string? userData,
        string? external, bool skipRecallFiles, CancellationToken token, Action<string>? watch = null, bool groupItems = true)
    {
        var snapshot = new ScreenshotDirectorySnapshot();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddFolder(string? path, bool isDefault = false, bool isInGame = false)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (Directory.Exists(fullPath) && seenFolders.Add(fullPath))
                    snapshot.Folders.Add(new(fullPath) { Default = isDefault, InGame = isInGame });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { snapshot.Errors.Add((path, ex)); }
        }
        AddFolder(backup, isDefault: true);
        AddFolder(inGame, isInGame: true);
        if (!string.IsNullOrWhiteSpace(userData))
        {
            AddFolder(Path.Combine(userData, "HoYoShade", "ScreenShot"));
            AddFolder(Path.Combine(userData, "OpenHoYoShade", "ScreenShot"));
        }
        foreach (string path in external?.Split(';') ?? Array.Empty<string>()) AddFolder(path.Trim());
        foreach (var folder in snapshot.Folders)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Start watching before enumeration; callbacks await the snapshot's
                // UI commit, so file arrivals cannot race collection construction.
                try { watch?.Invoke(folder.Folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { snapshot.Errors.Add((folder.Folder, ex)); }
                foreach (string file in Directory.EnumerateFiles(folder.Folder))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(file);
                    if (snapshot.ByName.ContainsKey(name) || !ScreenshotHelper.IsSupportedExtension(file)) continue;
                    try
                    {
                        if (skipRecallFiles && (File.GetAttributes(file) & (System.IO.FileAttributes)0x440000) != 0) continue;
                        snapshot.ByName.TryAdd(name, new ScreenshotItem(file));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { snapshot.Errors.Add((file, ex)); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { snapshot.Errors.Add((folder.Folder, ex)); }
        }
        token.ThrowIfCancellationRequested();
        var ordered = snapshot.ByName.Values.OrderByDescending(item => item.CreationTime).ToList();
        token.ThrowIfCancellationRequested();
        snapshot.Items = new(ordered);
        if (groupItems)
            snapshot.Groups = new(ordered.GroupBy(item => item.TimeMonthDay)
                .Select(group => new ScreenshotItemGroup(group.Key, group)));
        return snapshot;
    }
}

internal static class ScreenshotThumbnailViewport
{
    private sealed class State { public bool Visible; }
    private static readonly ConditionalWeakTable<CachedImage, State> States = new();

    public static void UpdateViewport(CachedImage image, EffectiveViewportChangedEventArgs args)
    {
        var viewport = args.EffectiveViewport;
        SetVisible(image, viewport.Width > 0 && viewport.Height > 0 &&
            viewport.Right > 0 && viewport.Bottom > 0 &&
            viewport.X < image.ActualWidth && viewport.Y < image.ActualHeight);
    }

    public static void SetVisible(CachedImage image, bool visible)
    {
        States.GetValue(image, static _ => new State()).Visible = visible;
        Refresh(image);
    }

    public static void Refresh(CachedImage image)
    {
        var state = States.GetValue(image, static _ => new State());
        // Source changes cancel ImageEx's obsolete request. Off-screen containers
        // keep their data identity, but no bitmap or pending thumbnail request.
        image.Source = state.Visible && image.IsLoaded && image.DataContext is ScreenshotItem item ? item.FilePath : null;
    }
}
