using System.Globalization;
using BiliNative.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliNative.App.Pages;

public sealed partial class SettingsPage : Page
{
    private AppUpdateInfo? _pendingUpdate;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings;
        DownloadDirectoryBox.Text = settings.DownloadDirectory;
        ConcurrencyBox.Text = settings.MaxConcurrentTasks.ToString(CultureInfo.InvariantCulture);
        RetryBox.Text = settings.MaxRetryAttempts.ToString(CultureInfo.InvariantCulture);
        FfmpegPathBox.Text = settings.FfmpegPath ?? string.Empty;
        MergeCheckBox.IsChecked = settings.MergeAfterDownload;
        DeleteTempCheckBox.IsChecked = settings.DeleteTemporaryFilesAfterMerge;
        AutoUpdateCheckBox.IsChecked = settings.AutoCheckUpdates;
        UpdateChannelBox.SelectedIndex = settings.UpdateChannel == UpdateChannel.Preview ? 0 : 1;
        if (AppServices.UpdateCoordinator.LatestUpdate is not null) SetUpdate(AppServices.UpdateCoordinator.LatestUpdate);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings;
        settings.DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectoryBox.Text) ? settings.DownloadDirectory : DownloadDirectoryBox.Text.Trim();
        if (int.TryParse(ConcurrencyBox.Text, out var concurrency)) settings.MaxConcurrentTasks = Math.Clamp(concurrency, 1, 3);
        if (int.TryParse(RetryBox.Text, out var retries)) settings.MaxRetryAttempts = Math.Clamp(retries, 1, 8);
        settings.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPathBox.Text) ? null : FfmpegPathBox.Text.Trim();
        settings.MergeAfterDownload = MergeCheckBox.IsChecked == true;
        settings.DeleteTemporaryFilesAfterMerge = DeleteTempCheckBox.IsChecked == true;
        settings.AutoCheckUpdates = AutoUpdateCheckBox.IsChecked == true;
        settings.UpdateChannel = (UpdateChannelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Stable" ? UpdateChannel.Stable : UpdateChannel.Preview;
        settings.Save();
        UpdateStatusText.Text = "设置已保存。";
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var update = await AppServices.UpdateCoordinator.CheckAsync();
            if (update is null) UpdateStatusText.Text = "当前没有可用更新，或更新源暂时不可访问。";
            else SetUpdate(update);
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"检查更新失败：{exception.Message}";
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在下载更新，完成后会自动覆盖当前版本并重启…";
        try
        {
            var progress = new Progress<double>(value => UpdateStatusText.Text = $"正在下载更新… {value:P0}");
            if (!await AppServices.UpdateCoordinator.DownloadAndApplyAsync(_pendingUpdate, progress)) UpdateStatusText.Text = "更新包不可用，请稍后重试。";
        }
        catch (Exception exception)
        {
            InstallUpdateButton.IsEnabled = true;
            UpdateStatusText.Text = $"更新失败：{exception.Message}";
        }
    }

    private void SetUpdate(AppUpdateInfo update)
    {
        _pendingUpdate = update;
        InstallUpdateButton.IsEnabled = update.SetupAsset is not null || update.PortableAsset is not null;
        UpdateStatusText.Text = $"发现 NovaClip {update.Version}，可以下载并覆盖当前版本。";
    }
}
