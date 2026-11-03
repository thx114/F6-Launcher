using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;

namespace HoYoShadeHub.Features.ViewHost;

public sealed partial class MainWindow
{
    private static readonly ILogger _responsivenessLog = AppConfig.GetLogger<MainWindow>();
    private bool _windowClosed;
    private bool _activationRefreshQueued;

    private void QueueActivationRefresh(DateTimeOffset now)
    {
        if (_windowClosed || _activationRefreshQueued) return;
        // WM_POINTERACTIVATE can be followed immediately by WM_ACTIVATE.
        if (now - _lastActivatedTime < TimeSpan.FromMilliseconds(300)) return;

        var message = new MainWindowStateChangedMessage
        {
            Activate = true,
            CurrentTime = now,
            LastActivatedTime = _lastActivatedTime,
        };
        var elapsed = Stopwatch.StartNew();
        _activationRefreshQueued = true;
        // First let WinUI process activation, input and painting. Subscribers may
        // check files or recreate a background, so do not run them inside WndProc.
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _activationRefreshQueued = false;
            if (_windowClosed) return;
            double queueMs = elapsed.Elapsed.TotalMilliseconds;
            var refresh = Stopwatch.StartNew();
            WeakReferenceMessenger.Default.Send(message);
            double refreshMs = refresh.Elapsed.TotalMilliseconds;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_windowClosed) return;
                double totalMs = elapsed.Elapsed.TotalMilliseconds;
                if (totalMs < 250 && !message.ElapsedOver(TimeSpan.FromSeconds(30))) return;
                using var process = Process.GetCurrentProcess();
                _responsivenessLog.LogInformation(
                    "Window activation: inactiveMs={InactiveMs:F0}, queueMs={QueueMs:F1}, subscribersMs={SubscribersMs:F1}, followupMs={FollowupMs:F1}, workingSetMiB={WorkingSetMiB:F1}, privateMiB={PrivateMiB:F1}, gen2={Gen2}",
                    (message.CurrentTime - message.LastActivatedTime).TotalMilliseconds,
                    queueMs, refreshMs, totalMs, process.WorkingSet64 / 1048576d,
                    process.PrivateMemorySize64 / 1048576d, GC.CollectionCount(2));
            });
        }))
        {
            _activationRefreshQueued = false;
            return;
        }
        _lastActivatedTime = now;
    }
}
