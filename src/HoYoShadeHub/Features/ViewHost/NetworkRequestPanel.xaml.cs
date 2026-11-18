using HoYoShadeHub.Core.Networking;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace HoYoShadeHub.Features.ViewHost;

public sealed partial class NetworkRequestPanel : UserControl
{
    private readonly ObservableCollection<NetworkRequestRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _expanded;
    public NetworkRequestPanel()
    {
        InitializeComponent();
        RequestList.ItemsSource = _rows;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }
    private void Summary_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        RequestDetails.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }
    private void Refresh()
    {
        var snapshot = NetworkRequestActivity.BackgroundSnapshot();
        int active = snapshot.Count(x => x.Active);
        PanelContent.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (active == 0) { _expanded = false; _rows.Clear(); return; }
        Summary.Content = $"后台请求 · {active} 项进行中";
        RequestDetails.Visibility = Visibility.Visible;
        var visible = (_expanded ? snapshot : snapshot.Where(x => x.Active).Take(5)).ToArray();
        for (int i = 0; i < visible.Length; i++)
        {
            if (i >= _rows.Count) _rows.Add(new(visible[i]));
            else if (_rows[i].Id != visible[i].Id) _rows[i] = new(visible[i]);
            else _rows[i].Update(visible[i]);
        }
        while (_rows.Count > visible.Length) _rows.RemoveAt(_rows.Count - 1);
    }
}

public sealed class NetworkRequestRow : INotifyPropertyChanged
{
    public long Id { get; }
    public string Endpoint { get; }
    public string Title { get; private set; } = string.Empty;
    public double Percent { get; private set; }
    public bool IsIndeterminate { get; private set; }
    public Visibility ProgressVisibility { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public NetworkRequestRow(NetworkRequestSnapshot snapshot)
    { Id = snapshot.Id; Endpoint = snapshot.Endpoint; Update(snapshot); }
    public void Update(NetworkRequestSnapshot snapshot)
    {
        Percent = snapshot.Percent ?? 0;
        IsIndeterminate = snapshot.Percent is null;
        ProgressVisibility = snapshot.IsDownload ? Visibility.Visible : Visibility.Collapsed;
        string progress = snapshot.IsDownload ? snapshot.Percent is { } percent
            ? $" · {percent:F0}% · {snapshot.BytesReceived / 1048576d:F1} MB"
            : $" · {snapshot.BytesReceived / 1048576d:F1} MB" : string.Empty;
        string title = $"{snapshot.Name} · {snapshot.Status}{progress} · {snapshot.ElapsedMilliseconds / 1000:F1}s";
        PropertyChanged?.Invoke(this, new(nameof(Percent)));
        PropertyChanged?.Invoke(this, new(nameof(IsIndeterminate)));
        PropertyChanged?.Invoke(this, new(nameof(ProgressVisibility)));
        if (Title == title) return;
        Title = title;
        PropertyChanged?.Invoke(this, new(nameof(Title)));
    }
}
