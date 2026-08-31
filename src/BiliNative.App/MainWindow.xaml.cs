using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliNative.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        ContentFrame.Navigate(typeof(Pages.BrowserPage));
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        var pageType = tag switch
        {
            "browser" => typeof(Pages.BrowserPage),
            "downloads" => typeof(Pages.DownloadsPage),
            "history" => typeof(Pages.HistoryPage),
            "settings" => typeof(Pages.SettingsPage),
            "about" => typeof(Pages.AboutPage),
            _ => typeof(Pages.BrowserPage)
        };
        if (ContentFrame.CurrentSourcePageType != pageType) ContentFrame.Navigate(pageType);
    }
}
