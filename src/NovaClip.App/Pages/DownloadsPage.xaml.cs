using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using NovaClip.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NovaClip.App.Pages;

public sealed partial class DownloadsPage : Page
{
    private readonly ObservableCollection<DownloadRow> _rows = [];
    private readonly Dictionary<Guid, DownloadRow> _rowById = [];
    private readonly ConcurrentDictionary<Guid, DownloadTaskSnapshot> _pendingSnapshots = new();
    private readonly DispatcherQueueTimer _coalesceTimer;
    private static readonly LocalizationService Text = new();

    public DownloadsPage()
    {
        InitializeComponent();
        TaskList.ItemsSource = _rows;
        foreach (var task in AppServices.Downloads.GetTasks()) AddRow(task);
        _coalesceTimer = DispatcherQueue.CreateTimer();
        _coalesceTimer.Interval = TimeSpan.FromMilliseconds(100);
        _coalesceTimer.IsRepeating = true;
        _coalesceTimer.Tick += CoalesceTimer_Tick;
        Loaded += (_, _) => _coalesceTimer.Start();
        Unloaded += DownloadsPage_Unloaded;
        AppServices.Downloads.TaskChanged += Downloads_TaskChanged;
        StartupDiagnostics.Info("DownloadsPage.Ready");
    }

    private void AddRow(DownloadTaskSnapshot snapshot)
    {
        if (_rowById.ContainsKey(snapshot.Id)) return;
        var row = new DownloadRow(snapshot);
        _rowById[snapshot.Id] = row;
        _rows.Add(row);
    }

    private void Downloads_TaskChanged(object? sender, DownloadTaskSnapshot snapshot) =>
        _pendingSnapshots[snapshot.Id] = snapshot;

    private void CoalesceTimer_Tick(DispatcherQueueTimer sender, object? args)
    {
        foreach (var pair in _pendingSnapshots.ToArray())
        {
            if (!_pendingSnapshots.TryRemove(pair.Key, out var snapshot)) continue;
            if (_rowById.TryGetValue(snapshot.Id, out var row))
            {
                row.Update(snapshot);
            }
            else
            {
                AddRow(snapshot);
            }
        }
    }

    private void DownloadsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _coalesceTimer.Stop();
        AppServices.Downloads.TaskChanged -= Downloads_TaskChanged;
    }

    private async void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadRow row) return;
        try
        {
            if (row.State is DownloadTaskState.Paused or DownloadTaskState.Failed) await AppServices.Downloads.ResumeAsync(row.Id);
            else if (row.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled)) await AppServices.Downloads.PauseAsync(row.Id);
            StatusBar.Message = Text.GetString("Task_StateUpdated");
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            StatusBar.Message = Text.Format("Error_WithCode", "TASK_ACTION_FAILED");
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
            StartupDiagnostics.Warning("Download task action failed.", exception);
        }
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadRow row) return;
        try
        {
            await AppServices.Downloads.CancelAsync(row.Id);
            StatusBar.Message = Text.GetString("Task_CancelRequested");
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            StatusBar.Message = Text.Format("Error_WithCode", "TASK_CANCEL_FAILED");
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
            StartupDiagnostics.Warning("Download cancellation failed.", exception);
        }
    }

    public sealed class DownloadRow : INotifyPropertyChanged
    {
        public DownloadRow(DownloadTaskSnapshot snapshot) => Snapshot = snapshot;

        public event PropertyChangedEventHandler? PropertyChanged;
        public DownloadTaskSnapshot Snapshot { get; private set; }
        public Guid Id => Snapshot.Id;
        public string Title => Snapshot.Title;
        public string OutputPath => Snapshot.OutputPath;
        public DownloadTaskState State => Snapshot.State;
        public string StateText
        {
            get
            {
                var localized = Text.GetString("DownloadState_" + State);
                return string.IsNullOrWhiteSpace(localized) ? State.ToString() : localized;
            }
        }
        public string? ErrorMessage => Snapshot.ErrorMessage;
        public double ProgressFraction => Snapshot.TotalBytes is > 0
            ? Math.Clamp((double)Snapshot.DownloadedBytes / Snapshot.TotalBytes.Value, 0, 1)
            : 0;

        public void Update(DownloadTaskSnapshot snapshot)
        {
            if (Snapshot == snapshot) return;
            Snapshot = snapshot;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
}
