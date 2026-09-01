using System.Globalization;
using NovaClip.Bilibili;
using NovaClip.Contracts;
using NovaClip.Core;
using NovaClip.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace NovaClip.App.Pages;

public sealed partial class BrowserPage : Page
{
    private readonly PlayUrlNormalizer _normalizer = new();
    private readonly BilibiliUrlResolver _urlResolver = new();
    private readonly BrowserNavigationPolicy _policy = new();
    private readonly BrowserHomeService _home = new();
    private readonly LocalizationService _text = new();
    private BilibiliPageContext? _pageContext;
    private MediaDescriptor? _currentMedia;
    private List<MediaTrack> _videoTracks = [];
    private Uri? _pendingExternalUri;
    private long _navigationGeneration;
    private bool _initialized;
    private bool _isLoading;

    public static BrowserPage? Current { get; private set; }

    public BrowserPage()
    {
        InitializeComponent();
        Loaded += BrowserPage_Loaded;
        Unloaded += (_, _) => { if (ReferenceEquals(Current, this)) Current = null; };
    }

    public void FocusAddressBar() { AddressBox.Focus(FocusState.Keyboard); AddressBox.SelectAll(); }
    public void Reload() { if (_isLoading) BrowserWebView.CoreWebView2?.Stop(); else BrowserWebView.CoreWebView2?.Reload(); }
    public void GoBack() { if (BrowserWebView.CoreWebView2?.CanGoBack == true) BrowserWebView.CoreWebView2.GoBack(); }
    public void GoForward() { if (BrowserWebView.CoreWebView2?.CanGoForward == true) BrowserWebView.CoreWebView2.GoForward(); }

    private async void BrowserPage_Loaded(object sender, RoutedEventArgs e)
    {
        Current = this;
        if (_initialized) return;
        _initialized = true;
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            SetDetectionState(MediaDetectionState.Observing);
            var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip", "WebView2");
            Directory.CreateDirectory(profilePath);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profilePath, null);
            await BrowserWebView.EnsureCoreWebView2Async(environment);
            var core = BrowserWebView.CoreWebView2;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.SourceChanged += Core_SourceChanged;
            core.HistoryChanged += Core_HistoryChanged;
            core.DocumentTitleChanged += Core_DocumentTitleChanged;
            core.ProcessFailed += Core_ProcessFailed;
            core.WebMessageReceived += Core_WebMessageReceived;
            core.WebResourceResponseReceived += Core_WebResourceResponseReceived;
            var bridgePath = Path.Combine(AppContext.BaseDirectory, "assets", "js", "bilibili-bridge.js");
            if (File.Exists(bridgePath)) await core.AddScriptToExecuteOnDocumentCreatedAsync(await File.ReadAllTextAsync(bridgePath));
            Navigate(_home.HomeUri);
            StartupDiagnostics.Info("WebView2.Ready");
            StartupDiagnostics.Info("BrowserPage.Ready");
        }
        catch (Exception exception)
        {
            ShowError("WEBVIEW_INITIALIZATION_FAILED", exception.Message);
        }
    }

    private void Core_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) return;
        var decision = _policy.Evaluate(uri, BrowserNavigationKind.NewWindow);
        if (decision == BrowserNavigationDecision.NavigateInCurrentView) sender.Navigate(uri.ToString());
        else PresentExternalNavigation(uri);
        StartupDiagnostics.Info("Browser.NewWindowIntercepted");
    }

    private void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || _policy.Evaluate(uri, BrowserNavigationKind.Redirect) != BrowserNavigationDecision.NavigateInCurrentView)
        {
            args.Cancel = true;
            if (uri is not null) PresentExternalNavigation(uri);
            return;
        }
        _navigationGeneration++;
        _pageContext = null;
        _currentMedia = null;
        _videoTracks.Clear();
        SetLoading(true);
        SetDetectionState(MediaDetectionState.WaitingForPageContext);
        StartupDiagnostics.Info("Browser.NavigationStarted");
    }

    private void Core_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        SetLoading(false);
        if (!args.IsSuccess) ShowError("BROWSER_NAVIGATION_FAILED", args.WebErrorStatus.ToString());
    }

    private void Core_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args) => DispatcherQueue.TryEnqueue(() => AddressBox.Text = sender.Source);
    private void Core_HistoryChanged(CoreWebView2 sender, object args) => DispatcherQueue.TryEnqueue(() => { BackButton.IsEnabled = sender.CanGoBack; ForwardButton.IsEnabled = sender.CanGoForward; });
    private void Core_DocumentTitleChanged(CoreWebView2 sender, object args) => StartupDiagnostics.Info("Browser.DocumentTitleChanged");
    private void Core_ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) => DispatcherQueue.TryEnqueue(() => ShowError("WEBVIEW_PROCESS_FAILED", args.ProcessFailedKind.ToString()));

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!BilibiliBridgeMessageParser.TryParse(args.WebMessageAsJson, out var message) || message is null) return;
        if (message.Type != BilibiliBridgeMessageType.PageContextChanged || !BilibiliBridgeMessageParser.TryReadPageContext(message, out var context)) return;
        _pageContext = context;
        DispatcherQueue.TryEnqueue(() => { TitleText.Text = context!.Title; IdentityText.Text = context.Bvid ?? string.Empty; SetDetectionState(MediaDetectionState.Observing); });
    }

    private async void Core_WebResourceResponseReceived(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (!args.Request.Uri.Contains("/playurl", StringComparison.OrdinalIgnoreCase)) return;
        var generation = _navigationGeneration;
        try
        {
            using var stream = await args.Response.GetContentAsync();
            using var reader = new StreamReader(stream.AsStreamForRead());
            var json = await reader.ReadToEndAsync();
            if (json.Length > 10_000_000 || generation != _navigationGeneration) return;
            var context = _pageContext is not null
                ? new PlayUrlContext(_pageContext.Url, _pageContext.Title, _pageContext.Bvid, _pageContext.Aid, _pageContext.Cid, _pageContext.EpisodeId, _pageContext.EpisodeTitle, _pageContext.Kind.Equals("bangumi", StringComparison.OrdinalIgnoreCase), ResolverStrategy.PlayUrlResponse)
                : new PlayUrlContext(sender.Source, _text.GetString("Browser_DefaultMediaTitle"));
            var result = _normalizer.Normalize(json, context);
            if (generation == _navigationGeneration) DispatcherQueue.TryEnqueue(() => ApplyResolveResult(result));
        }
        catch (Exception exception) { DispatcherQueue.TryEnqueue(() => ShowError("MEDIA_PLAYURL_READ_FAILED", exception.Message)); }
    }

    private void ApplyResolveResult(ResolveResult result)
    {
        if (!result.IsSuccess) { SetDetectionState(MediaDetectionState.Error); ShowError(result.Error?.Code ?? "MEDIA_NOT_FOUND", result.Error?.TechnicalMessage); return; }
        _currentMedia = result.Media;
        _videoTracks = result.Media!.Tracks.Where(track => track.Type == TrackType.Video).ToList();
        QualityCombo.Items.Clear();
        foreach (var track in _videoTracks) QualityCombo.Items.Add($"{QualityName(track.QualityId)} · {track.Codec ?? "—"} · {FormatBytes(track.Size)}");
        if (QualityCombo.Items.Count > 0) QualityCombo.SelectedIndex = 0;
        TitleText.Text = result.Media.Title;
        IdentityText.Text = result.Media.Bvid ?? result.Media.EpisodeId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TrackText.Text = $"{result.Media.VideoTrack?.Codec ?? "—"} · {result.Media.AudioTrack?.Codec ?? "—"}";
        AddDownloadButton.IsEnabled = _videoTracks.Count > 0;
        SetDetectionState(MediaDetectionState.Ready);
        StartupDiagnostics.Info("MediaDetection.Ready");
    }

    private async void AddDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMedia is null || _videoTracks.Count == 0) return;
        try
        {
            var video = _videoTracks[Math.Clamp(QualityCombo.SelectedIndex, 0, _videoTracks.Count - 1)];
            var audio = _currentMedia.Tracks.FirstOrDefault(track => track.Type == TrackType.Audio);
            var title = AppServices.FileNames.Sanitize(_currentMedia.Title, "Bilibili");
            var outputFile = AppServices.FileNames.GetAvailablePath(AppServices.Settings.DownloadDirectory, $"{title}.mp4");
            await AppServices.Downloads.EnqueueAsync(new DownloadRequest(Guid.NewGuid(), _currentMedia, video, audio, AppServices.Settings.DownloadDirectory, Path.GetFileName(outputFile), new RetryPolicy(AppServices.Settings.MaxRetryAttempts), AppServices.Settings.MergeAfterDownload, AppServices.Settings.DeleteTemporaryFilesAfterMerge));
            ShowInfo(_text.GetString("Download_Queued"));
        }
        catch (Exception exception) { ShowError("DOWNLOAD_CREATE_FAILED", exception.Message); }
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == global::Windows.System.VirtualKey.Enter && _urlResolver.TryResolve(AddressBox.Text, out var uri)) { Navigate(uri); e.Handled = true; } }
    private void Navigate(Uri uri) => BrowserWebView.CoreWebView2?.Navigate(uri.ToString());
    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => GoForward();
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();
    private void HomeButton_Click(object sender, RoutedEventArgs e) => Navigate(_home.HomeUri);
    private async void InfoActionButton_Click(object sender, RoutedEventArgs e) { if (_pendingExternalUri is not null) await global::Windows.System.Launcher.LaunchUriAsync(_pendingExternalUri); }

    private void SetLoading(bool value) { _isLoading = value; RefreshButton.Content = new SymbolIcon(value ? Symbol.Cancel : Symbol.Refresh); }
    private void SetDetectionState(MediaDetectionState state)
    {
        DetectionProgress.IsActive = state is MediaDetectionState.Resolving or MediaDetectionState.Observing;
        DetectionText.Text = _text.GetString(state switch { MediaDetectionState.Ready => "Detection_Ready", MediaDetectionState.Resolving or MediaDetectionState.Observing => "Detection_Observing", MediaDetectionState.PermissionDenied => "Detection_PermissionDenied", MediaDetectionState.Error => "Detection_Error", _ => "Detection_Empty" });
        MediaDetails.Visibility = state == MediaDetectionState.Ready ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PresentExternalNavigation(Uri uri) { _pendingExternalUri = uri; BrowserInfoBar.Severity = InfoBarSeverity.Informational; BrowserInfoBar.Message = _text.GetString("Browser_ExternalBlocked"); BrowserInfoBar.IsOpen = true; InfoActionButton.Visibility = Visibility.Visible; }
    private void ShowInfo(string message) { BrowserInfoBar.Severity = InfoBarSeverity.Success; BrowserInfoBar.Message = message; BrowserInfoBar.IsOpen = true; InfoActionButton.Visibility = Visibility.Collapsed; }
    private void ShowError(string code, string? detail) { BrowserInfoBar.Severity = InfoBarSeverity.Error; BrowserInfoBar.Message = _text.Format("Error_WithCode", code); BrowserInfoBar.IsOpen = true; InfoActionButton.Visibility = Visibility.Collapsed; StartupDiagnostics.Warning($"{code}: {detail}"); }
    private static string QualityName(int? id) => id switch { 127 => "8K", 126 => "Dolby Vision", 125 => "HDR", 120 => "4K", 116 => "1080P60", 112 => "1080P+", 80 => "1080P", 64 => "720P", 32 => "480P", 16 => "360P", _ => "Auto" };
    private static string FormatBytes(long? bytes) => bytes is null or <= 0 ? "—" : bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):F1} GB" : $"{bytes / (1024d * 1024):F0} MB";
}
