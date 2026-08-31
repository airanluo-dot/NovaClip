using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliNative.App;

public sealed partial class MainWindow : Window
{
    private readonly NavigationView _rootNavigationView;
    private readonly Frame _contentFrame;

    public MainWindow()
    {
        StartupDiagnostics.Info("Constructing MainWindow.");
        InitializeComponent();

        _contentFrame = new Frame();
        _rootNavigationView = new NavigationView
        {
            IsSettingsVisible = false,
            IsBackEnabled = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
            Content = _contentFrame
        };

        AddMenuItem("浏览器", "browser");
        AddMenuItem("下载", "downloads");
        AddMenuItem("历史", "history");
        AddMenuItem("设置", "settings");
        AddMenuItem("关于", "about");

        _rootNavigationView.SelectionChanged += RootNavigationView_SelectionChanged;
        Content = _rootNavigationView;

        _rootNavigationView.SelectedItem = _rootNavigationView.MenuItems[0];
        NavigateTo("browser");
        StartupDiagnostics.Info("MainWindow constructed successfully.");
    }

    private void AddMenuItem(string label, string tag)
    {
        _rootNavigationView.MenuItems.Add(new NavigationViewItem
        {
            Content = label,
            Tag = tag
        });
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "browser" => typeof(Pages.BrowserPage),
            "downloads" => typeof(Pages.DownloadsPage),
            "history" => typeof(Pages.HistoryPage),
            "settings" => typeof(Pages.SettingsPage),
            "about" => typeof(Pages.AboutPage),
            _ => typeof(Pages.BrowserPage)
        };

        if (_contentFrame.CurrentSourcePageType == pageType) return;

        try
        {
            StartupDiagnostics.Info($"Navigating to {pageType.Name}.");
            if (!_contentFrame.Navigate(pageType))
            {
                throw new InvalidOperationException($"Frame.Navigate returned false for {pageType.Name}.");
            }
            StartupDiagnostics.Info($"Navigation completed: {pageType.Name}.");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error($"Navigation failed: {pageType.Name}.", exception);
            _contentFrame.Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Padding = new Thickness(24),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{pageType.Name} 无法加载",
                            FontSize = 28,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Text = exception.ToString(),
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        },
                        new TextBlock
                        {
                            Text = $"日志：{StartupDiagnostics.LogPath}",
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                            Opacity = 0.7
                        }
                    }
                }
            };
        }
    }
}
