using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Core.Networking;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Threading;

namespace HoYoShadeHub.Frameworks;

[INotifyPropertyChanged]
public abstract partial class PageBase : Page
{
    private readonly UiLifetime _lifetime = new();
    private readonly string _operationOwner = Guid.NewGuid().ToString("N");
    private int _retainedOperations;
    protected bool HasPageOperation => _retainedOperations > 0;
    protected CancellationToken PageCancellationToken => _lifetime.Token;
    protected long PageGeneration => _lifetime.Generation;
    protected bool IsPageGenerationCurrent(long generation) => _lifetime.IsCurrent(generation);


    public GameId CurrentGameId { get; protected set => SetProperty(ref field, value); }


    public GameBiz CurrentGameBiz { get; protected set => SetProperty(ref field, value); }



    public PageBase()
    {
        // Cache only until navigation decides whether an operation still needs this page.
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += PageEx_Loaded;
        Unloaded += PageEx_Unloaded;
    }



    private void PageEx_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_lifetime.Enter()) return;
        BackgroundOperationActivity.SetOwnerVisible(_operationOwner, true);
        long generation = PageGeneration;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsPageGenerationCurrent(generation)) return;
            try { OnLoaded(); }
            catch { _lifetime.Exit(); throw; }
        });
    }


    private void PageEx_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        BackgroundOperationActivity.SetOwnerVisible(_operationOwner, false);
        if (_lifetime.Exit()) OnUnloaded();
        if (!HasPageOperation) NavigationCacheMode = NavigationCacheMode.Disabled;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (!HasPageOperation) NavigationCacheMode = NavigationCacheMode.Disabled;
        base.OnNavigatedFrom(e);
    }

    /// <summary>Retain only pages participating in a running operation, including a settings parent.</summary>
    protected IDisposable BeginPageOperation(string name)
    {
        List<PageBase> pages = [];
        for (Microsoft.UI.Xaml.DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is PageBase page) { page._retainedOperations++; pages.Add(page); }
        var activity = BackgroundOperationActivity.Begin(_operationOwner, name, IsLoaded);
        return new OperationLease(pages, activity);
    }

    private sealed class OperationLease(List<PageBase> pages, IDisposable activity) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            activity.Dispose();
            foreach (var page in pages)
                if (--page._retainedOperations == 0 && !page.IsLoaded)
                    page.NavigationCacheMode = NavigationCacheMode.Disabled;
        }
    }



    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is GameId id)
        {
            CurrentGameId = id;
            CurrentGameBiz = id.GameBiz;
        }
        else if (e.Parameter is GameBiz biz)
        {
            CurrentGameBiz = biz;
            CurrentGameId = GameId.FromGameBiz(biz)!;
        }
        else
        {
            CurrentGameId = null!;
            CurrentGameBiz = default;
        }
    }



    protected virtual void OnLoaded()
    {

    }



    protected virtual void OnUnloaded()
    {

    }


}
