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
        Title = new LocalizationService().GetString("MainWindow_Title");
        TryConfigureBackdrop();
        ApplyTheme(AppServices.Settings.Theme);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        NavigateTo("browser");
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        InstallKeyboardAccelerators();
        Closed += MainWindow_Closed;
        StartupDiagnostics.Info("Shell.Ready");
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        StartupDiagnostics.Info("MainWindow.Closed");
        AppServices.BeginShutdown();
        (Microsoft.UI.Xaml.Application.Current as App)?.DisposeSingleInstance();
    }

    private void TryConfigureBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
            StartupDiagnostics.Info("Shell.BackdropReady");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Shell backdrop unavailable; using the default background.", exception);
        }
    }

    public async Task RunSmokeNavigationAsync()
    {
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1200, 900));
        await Task.Delay(300);
        var browser = Pages.BrowserPage.Instance ?? throw new InvalidOperationException("BROWSER_CACHE_MISSING");
        foreach (var tag in new[] { "downloads", "history", "settings", "browser" })
        {
            NavigateTo(tag);
            // Allow Loaded/Unloaded to run before checking cached ownership.
            await Task.Delay(700);
            if (!ReferenceEquals(Pages.BrowserPage.Instance, browser))
                throw new InvalidOperationException("BROWSER_CACHE_OWNERSHIP_LOST");
            if (tag != "browser") await CaptureSmokePageAsync(tag);
            if (ContentFrame.Content is Pages.DownloadsPage downloads)
            {
                downloads.ShowSmokePreview();
                await Task.Delay(300);
                await CaptureSmokePageAsync("downloads-populated");
                RootNavigationView.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(300);
                await CaptureSmokePageAsync("downloads-dark");
                RootNavigationView.RequestedTheme = ElementTheme.Light;
            }
        }
        StartupDiagnostics.Info("Browser.CacheOwnershipVerified");
    }

    private async Task CaptureSmokePageAsync(string tag)
    {
        // CI-only, freshly initialized test profile; never capture a user's session.
        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            ContentFrame.UpdateLayout();
            await bitmap.RenderAsync(ContentFrame.Content as UIElement ?? ContentFrame);
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
                throw new InvalidOperationException("UI_CAPTURE_EMPTY");
            var pixels = await bitmap.GetPixelsAsync();
            var bytes = new byte[pixels.Length];
            using (var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(pixels))
                reader.ReadBytes(bytes);
            var directory = Path.Combine(AppContext.BaseDirectory, "ui-smoke");
            Directory.CreateDirectory(directory);
            var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync(tag + ".png", global::Windows.Storage.CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(global::Windows.Storage.FileAccessMode.ReadWrite);
            var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            StartupDiagnostics.Info("UI.Captured:" + tag);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("UI capture unavailable: " + tag, exception);
        }
    }

    public void ApplyTheme(string theme)
    {
        RootNavigationView.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
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
        if (!ContentFrame.Navigate(pageType)) throw new InvalidOperationException("NAVIGATION_FAILED:" + pageType.Name);
        StartupDiagnostics.Info(pageType.Name + ".Ready");
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
