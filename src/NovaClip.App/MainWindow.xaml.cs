using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = global::Windows.System.VirtualKey;
using VirtualKeyModifiers = global::Windows.System.VirtualKeyModifiers;

namespace NovaClip.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        StartupDiagnostics.Info("MainWindow.Created");
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        ContentFrame.Navigate(typeof(Pages.BrowserPage));
        InstallKeyboardAccelerators();
        StartupDiagnostics.Info("Shell.Ready");
        if (Environment.GetEnvironmentVariable("NOVACLIP_CI_SMOKE") == "1")
        {
            foreach (var tag in new[] { "downloads", "history", "settings", "browser" }) NavigateTo(tag);
        }
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { NavigateTo("settings"); return; }
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }) NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "browser" => typeof(Pages.BrowserPage),
            "downloads" => typeof(Pages.DownloadsPage),
            "history" => typeof(Pages.HistoryPage),
            "settings" => typeof(Pages.SettingsPage),
            _ => typeof(Pages.BrowserPage)
        };
        if (ContentFrame.CurrentSourcePageType == pageType) return;
        if (!ContentFrame.Navigate(pageType)) throw new InvalidOperationException($"NAVIGATION_FAILED:{pageType.Name}");
        StartupDiagnostics.Info($"{pageType.Name}.Ready");
    }

    private void InstallKeyboardAccelerators()
    {
        AddAccelerator(VirtualKeyModifiers.Control, (VirtualKey)188, () => NavigateTo("settings"));
        AddAccelerator(VirtualKeyModifiers.Control, VirtualKey.L, () => Pages.BrowserPage.Current?.FocusAddressBar());
        AddAccelerator(VirtualKeyModifiers.Control, VirtualKey.R, () => Pages.BrowserPage.Current?.Reload());
        AddAccelerator(VirtualKeyModifiers.Menu, VirtualKey.Left, () => Pages.BrowserPage.Current?.GoBack());
        AddAccelerator(VirtualKeyModifiers.Menu, VirtualKey.Right, () => Pages.BrowserPage.Current?.GoForward());
    }

    private void AddAccelerator(VirtualKeyModifiers modifiers, VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Modifiers = modifiers, Key = key };
        accelerator.Invoked += (_, args) => { action(); args.Handled = true; };
        RootNavigationView.KeyboardAccelerators.Add(accelerator);
    }
}
