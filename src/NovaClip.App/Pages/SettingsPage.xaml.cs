using System.Globalization;
using NovaClip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Storage.Pickers;

namespace NovaClip.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly LocalizationService _text = new();
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings;
        DownloadDirectoryText.Text = settings.DownloadDirectory;
        ConcurrencyButtons.SelectedItem = FindByTag(ConcurrencyButtons.Items, settings.MaxConcurrentTasks.ToString(CultureInfo.InvariantCulture));
        QualityBox.SelectedItem = FindByTag(QualityBox.Items, settings.DefaultQuality);
        CodecBox.SelectedItem = FindByTag(CodecBox.Items, settings.DefaultCodec);
        RetryBox.SelectedItem = FindByTag(RetryBox.Items, settings.RetryPreset);
        StartupBox.SelectedItem = FindByTag(StartupBox.Items, settings.BrowserStartup);
        ExternalLinksBox.SelectedItem = FindByTag(ExternalLinksBox.Items, settings.ExternalLinkBehavior);
        MergeToggle.IsOn = settings.MergeAfterDownload;
        DeleteTempToggle.IsOn = settings.DeleteTemporaryFilesAfterMerge;
        AutoUpdateToggle.IsOn = settings.AutoCheckUpdates;
        DebugToggle.IsOn = settings.DebugLogging;
        ChannelButtons.SelectedItem = FindByTag(ChannelButtons.Items, settings.UpdateChannel.ToString());
        ThemeBox.SelectedIndex = 0;
        RefreshFfmpegStatus();
        _loading = false;
    }

    private void ImmediateSetting_Changed(object sender, object e)
    {
        if (_loading) return;
        var settings = AppServices.Settings;
        if (ConcurrencyButtons.SelectedItem is RadioButton concurrency && int.TryParse(concurrency.Tag?.ToString(), out var count)) settings.MaxConcurrentTasks = count;
        settings.DefaultQuality = SelectedTag(QualityBox) ?? settings.DefaultQuality;
        settings.DefaultCodec = SelectedTag(CodecBox) ?? settings.DefaultCodec;
        settings.RetryPreset = SelectedTag(RetryBox) ?? settings.RetryPreset;
        settings.MaxRetryAttempts = settings.RetryPreset switch { "Aggressive" => 6, "Off" => 1, _ => 3 };
        settings.BrowserStartup = SelectedTag(StartupBox) ?? settings.BrowserStartup;
        settings.ExternalLinkBehavior = SelectedTag(ExternalLinksBox) ?? settings.ExternalLinkBehavior;
        settings.MergeAfterDownload = MergeToggle.IsOn;
        settings.DeleteTemporaryFilesAfterMerge = DeleteTempToggle.IsOn;
        settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        settings.DebugLogging = DebugToggle.IsOn;
        if (ChannelButtons.SelectedItem is RadioButton channel && Enum.TryParse<UpdateChannel>(channel.Tag?.ToString(), out var parsed)) settings.UpdateChannel = parsed;
        settings.Save();
        SettingsInfoBar.Severity = InfoBarSeverity.Success;
        SettingsInfoBar.Message = _text.GetString("Settings_Saved");
        SettingsInfoBar.IsOpen = true;
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        AppServices.Settings.DownloadDirectory = folder.Path;
        DownloadDirectoryText.Text = folder.Path;
        AppServices.Settings.Save();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(AppServices.Settings.DownloadDirectory)) await global::Windows.System.Launcher.LaunchFolderPathAsync(AppServices.Settings.DownloadDirectory); }
    private async void ClearLogin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = _text.GetString("Settings_ClearLoginTitle"), Content = _text.GetString("Settings_ClearLoginMessage"), PrimaryButtonText = _text.GetString("Common_Clear"), CloseButtonText = _text.GetString("Common_Cancel"), DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip", "WebView2");
        if (Directory.Exists(profile)) Directory.Delete(profile, true);
    }
    private void DetectFfmpeg_Click(object sender, RoutedEventArgs e) { AppServices.Settings.FfmpegPath = null; RefreshFfmpegStatus(); }
    private async void ChooseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)); var file = await picker.PickSingleFileAsync(); if (file is null) return; AppServices.Settings.FfmpegPath = file.Path; AppServices.Settings.Save(); RefreshFfmpegStatus();
    }
    private void TestFfmpeg_Click(object sender, RoutedEventArgs e) => RefreshFfmpegStatus();
    private async void OpenLogs_Click(object sender, RoutedEventArgs e) { var folder = Path.GetDirectoryName(StartupDiagnostics.LogPath)!; await global::Windows.System.Launcher.LaunchFolderPathAsync(folder); }
    private void ResetDetector_Click(object sender, RoutedEventArgs e) => BrowserPage.Current?.Reload();
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        SettingsInfoBar.Message = _text.GetString("Update_Checking"); SettingsInfoBar.IsOpen = true;
        var update = await AppServices.UpdateCoordinator.CheckAsync();
        SettingsInfoBar.Message = update is null ? _text.GetString("Update_None") : _text.Format("Update_Found", update.Version);
    }
    private void RefreshFfmpegStatus() { FfmpegPathText.Text = AppServices.Settings.FfmpegPath ?? _text.GetString("Settings_FfmpegMissing"); }
    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    private static object? FindByTag(IEnumerable<object> items, string tag) => items.FirstOrDefault(item => item is FrameworkElement element && string.Equals(element.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
}
