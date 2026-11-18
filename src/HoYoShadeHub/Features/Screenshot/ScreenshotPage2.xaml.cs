using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using HoYoShadeHub.Core;
using HoYoShadeHub.Controls;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.HoYoPlay;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;


namespace HoYoShadeHub.Features.Screenshot;

public sealed partial class ScreenshotPage2 : PageBase
{


    private readonly ILogger<ScreenshotPage2> _logger = AppConfig.GetLogger<ScreenshotPage2>();


    private readonly HoYoPlayService _hoyoplayService = AppConfig.GetService<HoYoPlayService>();




    public ScreenshotPage2()
    {
        this.InitializeComponent();
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
            Screenshots = null!;
            _defaultScrollController = null!;
            _detailLabelToolTip?.Content = null;
            _detailLabelToolTip?.IsOpen = false;
            _detailLabelToolTip = null;
            AnnotatedScrollBar.Labels.Clear();
        }
        catch { }
    }



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

    // FileSystemWatcher 的回调在线程池线程上跑，会并发碰这个字典 —— 必须线程安全
    private ConcurrentDictionary<string, ScreenshotItem> _screenshotDict = new();

    public ObservableCollection<ScreenshotItem> Screenshots { get; set => SetProperty(ref field, value); }


    private IScrollController _defaultScrollController;

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
            Screenshots = null!;

            string? externalFolder = AppConfig.GetExternalScreenshotFolder(CurrentGameBiz);
            string? userDataFolder = AppConfig.UserDataFolder;
            (string? backupFolder, string? screenshotFolder) = await GetGameScreenshotPathAsync(token);
            var snapshot = await Task.Run(() => ScreenshotDirectorySnapshot.Read(
                backupFolder, screenshotFolder, userDataFolder, externalFolder, true, token,
                folder => pendingWatchers.Add(CreateFileSystemWatcher(folder, token, revision, ready.Task)), groupItems: false), token);
            if (!IsScreenshotLoadCurrent(revision, token)) return;
            _folders = snapshot.Folders;
            _screenshotDict = snapshot.ByName;
            Screenshots = snapshot.Items;
            _watchers = pendingWatchers;
            pendingWatchers = new();
            ready.TrySetResult();
            foreach (var error in snapshot.Errors)
                _logger.LogWarning(error.Error, "Read screenshot directory {Path}", error.Path);
            await Task.Delay(100, token);
            if (!IsScreenshotLoadCurrent(revision, token)) return;
            UpdateLabels();
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



    private FileSystemWatcher CreateFileSystemWatcher(string folder, CancellationToken token, int revision, Task ready)
    {
        var watcher = new FileSystemWatcher(folder);
        watcher.NotifyFilter = NotifyFilters.FileName;
        foreach (var filter in ScreenshotHelper.WatcherFilters) watcher.Filters.Add(filter);
        watcher.Created += (_, e) => FileSystemWatcher_Created(e, token, revision, ready);
        watcher.Deleted += (_, e) => FileSystemWatcher_Deleted(e, token, revision, ready);
        watcher.EnableRaisingEvents = true;
        return watcher;
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



    private void ItemsView_Images_Loaded(object sender, RoutedEventArgs e)
    {
        _defaultScrollController ??= ItemsView_Images.VerticalScrollController;
    }
    
    

    private void AnnotatedScrollBar_DetailLabelRequested(AnnotatedScrollBar sender, AnnotatedScrollBarDetailLabelRequestedEventArgs args)
    {
        try
        {
            if (Screenshots is not null)
            {
                double offset = 0;
                double viewportHeight = ItemsView_Images.ScrollView.ViewportHeight;
                double extentHeight = ItemsView_Images.ScrollView.ExtentHeight;
                if (args.ScrollOffset < viewportHeight / 2 || args.ScrollOffset > extentHeight - viewportHeight / 2)
                {
                    offset = args.ScrollOffset / ItemsView_Images.ScrollView.ExtentHeight;
                }
                else
                {
                    offset = (args.ScrollOffset + viewportHeight / 2) / ItemsView_Images.ScrollView.ExtentHeight;
                }
                if (offset < 0 || offset > 1)
                {
                    args.Content = null;
                    _detailLabelToolTip?.IsOpen = false;
                }
                else
                {
                    int index = (int)Math.Ceiling(offset * (Screenshots.Count - 1));
                    index = Math.Clamp(index, 0, Screenshots.Count - 1);
                    args.Content = Screenshots[index].CreationTime.ToString("yyyy-MM-dd");
                }
            }
        }
        catch { }
    }



    private void UpdateLabels()
    {
        try
        {
            if (Screenshots?.Count > 0)
            {
                double count = Screenshots.Count;
                if (ItemsView_Images.ScrollView.ExtentHeight / ItemsView_Images.ScrollView.ViewportHeight > 3)
                {
                    AnnotatedScrollBar.Labels.Clear();
                    double extentHeight = ItemsView_Images.ScrollView.ExtentHeight;

                    int lastYear = int.MinValue;
                    int lastYearMonth = int.MinValue;
                    var labels = new List<AnnotatedScrollBarLabel>();
                    for (int i = Screenshots.Count - 1; i >= 0; i--)
                    {
                        int year = Screenshots[i].CreationTime.Year;
                        int yearMonth = year * 100 + Screenshots[i].CreationTime.Month;
                        if (year > lastYear)
                        {
                            double offset = extentHeight * i / count;
                            labels.Add(new AnnotatedScrollBarLabel(year.ToString(), offset));
                            lastYear = year;
                            lastYearMonth = yearMonth;
                        }
                        else if (yearMonth > lastYearMonth)
                        {
                            double offset = extentHeight * i / count;
                            lastYearMonth = yearMonth;
                            labels.Add(new AnnotatedScrollBarLabel("•", offset));
                        }
                    }

                    double lastOffset = double.MaxValue;
                    bool lastIsYear = false;
                    foreach (var item in labels)
                    {
                        if (item.Content is not "•")
                        {
                            AnnotatedScrollBar.Labels.Insert(0, item);
                            lastOffset = item.ScrollOffset;
                            lastIsYear = true;
                        }
                        else if (lastIsYear)
                        {
                            if (Math.Abs(item.ScrollOffset - lastOffset) / extentHeight >= 0.04)
                            {
                                AnnotatedScrollBar.Labels.Insert(0, item);
                                lastOffset = item.ScrollOffset;
                                lastIsYear = false;
                            }
                        }
                        else
                        {
                            AnnotatedScrollBar.Labels.Insert(0, item);
                            lastOffset = item.ScrollOffset;
                        }
                    }

                    AnnotatedScrollBar.Opacity = 1;
                    AnnotatedScrollBar.IsHitTestVisible = true;
                    AnnotatedScrollBar.Visibility = Visibility.Visible;
                    ItemsView_Images.VerticalScrollController = AnnotatedScrollBar.ScrollController;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update labels");
        }
        if (_defaultScrollController is not null)
        {
            AnnotatedScrollBar.Visibility = Visibility.Collapsed;
            ItemsView_Images.VerticalScrollController = _defaultScrollController;
        }
    }



    private async void FileSystemWatcher_Created(FileSystemEventArgs e, CancellationToken token, int revision, Task ready)
    {
        try
        {
            await ready.WaitAsync(token);
            if (ScreenshotHelper.IsSupportedExtension(e.FullPath) && File.Exists(e.FullPath))
            {
                string name = Path.GetFileName(e.FullPath);
                await ScreenshotHelper.WaitForFileReleaseAsync(e.FullPath, token);
                token.ThrowIfCancellationRequested();
                var item = new ScreenshotItem(e.FullPath);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsScreenshotLoadCurrent(revision, token) || !_screenshotDict.TryAdd(name, item)) return;
                    Screenshots ??= new();
                    Screenshots.Insert(0, item);
                    UpdateLabels();
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
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsScreenshotLoadCurrent(revision, token)) return;
                if (Screenshots?.FirstOrDefault(x => x.FilePath == e.FullPath) is ScreenshotItem item)
                {
                    _screenshotDict.TryRemove(Path.GetFileName(e.FullPath), out _);
                    Screenshots.Remove(item);
                    UpdateLabels();
                }
            });
        }
        catch { }
    }





    #region Action


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
            }
        }
        catch { }
    }



    private async void Button_CopyImage_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button)
        {
            if (button.DataContext is ScreenshotItem item)
            {
                try
                {
                    _logger.LogInformation("Copy image: {file}", item.FilePath);
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    ClipboardHelper.SetStorageItems(DataPackageOperation.Copy, file);
                    button.Content = new FontIcon { Glyph = "\uE8FB", FontSize = 16 };
                    await Task.Delay(3000);
                    button.Content = new FontIcon { Glyph = "\uE8C8", FontSize = 16 };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Copy image");
                }
            }
        }
    }



    private void Grid_ImageItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement grid && grid.DataContext is ScreenshotItem item)
            {
                _ = new ImageViewWindow2().ShowWindowAsync(this.XamlRoot.ContentIslandEnvironment.AppWindowId, item, Screenshots);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open ImageViewWindow");
        }
    }



    private async void Grid_ImageItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        try
        {
            if (sender is FrameworkElement grid && grid.DataContext is ScreenshotItem item)
            {
                var deferral = args.GetDeferral();
                try
                {
                    args.AllowedOperations = DataPackageOperation.Copy;
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    args.Data.SetStorageItems([file], true);
                }
                finally
                {
                    // 中途抛（比如文件刚被删）也必须 Complete，否则拖放源一直挂着
                    deferral.Complete();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Drag image starting");
        }
    }






    #endregion




    #region AnnotatedScrollBar.DetailLabelToolTip

    // https://github.com/microsoft/microsoft-ui-xaml/issues/9726


    private ToolTip? _detailLabelToolTip;

    private bool _isPointerHovered;

    private bool _isPointerPressed;


    private void AnnotatedScrollBar_Loaded(object sender, RoutedEventArgs e)
    {
        if (AnnotatedScrollBar.FindDescendant("PART_ToolTipRail") is DependencyObject border)
        {
            if (ToolTipService.GetToolTip(border) is ToolTip toolTip)
            {
                _detailLabelToolTip = toolTip;
            }
        }
    }



    private void AnnotatedScrollBar_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerHovered = true;
    }


    private void AnnotatedScrollBar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPointerPressed)
        {
            _detailLabelToolTip?.IsOpen = false;
        }
        _isPointerHovered = false;
    }


    private void AnnotatedScrollBar_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerPressed = true;
    }

    private void AnnotatedScrollBar_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPointerHovered)
        {
            _detailLabelToolTip?.IsOpen = false;
        }
        _isPointerPressed = false;
    }






    #endregion




}
