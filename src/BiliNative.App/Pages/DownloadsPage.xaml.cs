using System.Collections.ObjectModel;
using BiliNative.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliNative.App.Pages;

public sealed partial class DownloadsPage : Page
{
    private readonly ObservableCollection<DownloadRow> _rows = [];

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
        if (TaskList.SelectedItem is not DownloadRow row) return;
        if (row.State == DownloadTaskState.Paused) await AppServices.Downloads.ResumeAsync(row.Id);
        else if (row.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled or DownloadTaskState.Failed)) await AppServices.Downloads.PauseAsync(row.Id);
        StatusText.Text = "已更新任务状态。";
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not DownloadRow row) return;
        await AppServices.Downloads.CancelAsync(row.Id);
        StatusText.Text = "已请求取消任务。";
    }

    public sealed record DownloadRow(DownloadTaskSnapshot Snapshot)
    {
        public Guid Id => Snapshot.Id;
        public string Title => Snapshot.Title;
        public string OutputPath => Snapshot.OutputPath;
        public DownloadTaskState State => Snapshot.State;
        public string? ErrorMessage => Snapshot.ErrorMessage;
        public double ProgressFraction => Snapshot.TotalBytes is > 0 ? Math.Clamp((double)Snapshot.DownloadedBytes / Snapshot.TotalBytes.Value, 0, 1) : 0;
    }
}
