using System.Collections.ObjectModel;
using NovaClip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NovaClip.App.Pages;

public sealed partial class HistoryPage : Page
{
    private const int PageSize = 100;
    private readonly ObservableCollection<DownloadTaskSnapshot> _items = [];
    private ScrollViewer? _scrollViewer;
    private DateTimeOffset? _beforeUpdatedAt;
    private Guid? _beforeId;
    private bool _hasMore = true;
    private bool _loading;

    public HistoryPage()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _items;
        Loaded += HistoryPage_Loaded;
        Unloaded += HistoryPage_Unloaded;
    }

    private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer is null)
        {
            _scrollViewer = FindScrollViewer(HistoryList);
            if (_scrollViewer is not null) _scrollViewer.ViewChanged += HistoryScrollViewer_ViewChanged;
        }

        if (_items.Count == 0) await LoadNextPageAsync();
        StartupDiagnostics.Info("HistoryPage.Ready");
    }

    private void HistoryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer is not null) _scrollViewer.ViewChanged -= HistoryScrollViewer_ViewChanged;
        _scrollViewer = null;
    }

    private void HistoryScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_hasMore && !_loading && e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 240)
        {
            _ = LoadNextPageFromUiAsync();
        }
    }

    private async Task LoadNextPageFromUiAsync()
    {
        try { await LoadNextPageAsync(); }
        catch (Exception exception) { StartupDiagnostics.Warning("History page could not be loaded.", exception); }
    }

    private async Task LoadNextPageAsync()
    {
        if (_loading || !_hasMore) return;
        _loading = true;
        try
        {
            var page = await ((IHistoryRepository)AppServices.Repository).GetPageAsync(PageSize, _beforeUpdatedAt, _beforeId);
            foreach (var item in page.Items) _items.Add(item);
            _beforeUpdatedAt = page.NextUpdatedAt;
            _beforeId = page.NextId;
            _hasMore = page.HasMore;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("History could not be loaded.", exception);
            _hasMore = false;
            if (_items.Count == 0) HistoryList.ItemsSource = Array.Empty<DownloadTaskSnapshot>();
        }
        finally
        {
            _loading = false;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
