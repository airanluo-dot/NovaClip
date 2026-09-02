using System.Collections.ObjectModel;
using NovaClip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NovaClip.App.Pages;

public sealed partial class DownloadsPage : Page
{
    private readonly ObservableCollection<DownloadRow> _rows = [];
    private static readonly LocalizationService Text = new();

    public DownloadsPage()
    {
        InitializeComponent();
        TaskList.ItemsSource = _rows;
        foreach (var task in AppServices.Downloads.GetTasks()) _rows.Add(new DownloadRow(task));
        AppServices.Downloads.TaskChanged += Downloads_TaskChanged;
        Unloaded += (_, _) => AppServices.Downloads.TaskChanged -= Downloads_TaskChanged;
    }

    private void Downloads_TaskChanged(object? sender, DownloadTaskSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var existing = _rows.FirstOrDefault(row => row.Id == snapshot.Id);
            if (existing is null) _rows.Insert(0, new DownloadRow(snapshot));
            else _rows[_rows.IndexOf(existing)] = new DownloadRow(snapshot);
        });
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

    public sealed record DownloadRow(DownloadTaskSnapshot Snapshot)
    {
        public Guid Id => Snapshot.Id;
        public string Title => Snapshot.Title;
        public string OutputPath => Snapshot.OutputPath;
        public DownloadTaskState State => Snapshot.State;
        public string StateText
        {
            get
            {
                var localized = Text.GetString($"DownloadState_{State}");
                return string.IsNullOrWhiteSpace(localized) ? State.ToString() : localized;
            }
        }
        public string? ErrorMessage => Snapshot.ErrorMessage;
        public double ProgressFraction => Snapshot.TotalBytes is > 0 ? Math.Clamp((double)Snapshot.DownloadedBytes / Snapshot.TotalBytes.Value, 0, 1) : 0;
    }
}
