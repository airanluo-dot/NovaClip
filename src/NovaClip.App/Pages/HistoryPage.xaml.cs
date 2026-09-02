using NovaClip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NovaClip.App.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        Loaded += HistoryPage_Loaded;
    }

    private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var history = await ((IHistoryRepository)AppServices.Repository).GetAllAsync();
            HistoryList.ItemsSource = history;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("History could not be loaded.", exception);
            HistoryList.ItemsSource = Array.Empty<DownloadTaskSnapshot>();
        }
    }
}
