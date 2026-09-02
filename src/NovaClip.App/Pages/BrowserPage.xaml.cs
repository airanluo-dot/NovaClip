using System.Globalization;
using System.Text;
using System.Text.Json;
using NovaClip.Bilibili;
using NovaClip.Contracts;
using NovaClip.Core;
using NovaClip.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;

namespace NovaClip.App.Pages;

public sealed partial class BrowserPage : Page
{
    private const int MaxPlayUrlResponseCharacters = 10_000_000;
    private const int MaxCookieHeaderCharacters = 64_000;
    private readonly PlayUrlNormalizer _normalizer = new();
    private readonly BilibiliUrlResolver _urlResolver = new();
    private readonly BrowserNavigationPolicy _policy = new();
    private readonly BrowserHomeService _home = new();
    private readonly LocalizationService _text = new();
    private BilibiliPageContext? _pageContext;
    private MediaDescriptor? _currentMedia;
    private List<MediaTrack> _videoTracks = [];
    private Uri? _pendingExternalUri;
    private bool _webViewRecoveryRequested;
    private long _navigationGeneration;
    private Task? _initializationTask;
    private bool _isLoading;
    private static readonly object EnvironmentGate = new();
    private static Task<CoreWebView2Environment>? SharedEnvironmentTask;

    public static BrowserPage? Current { get; private set; }
    public static BrowserPage? Instance { get; private set; }
    public bool HasInitializedWebView => BrowserWebView.CoreWebView2 is not null;

    public BrowserPage()
    {
        InitializeComponent();
        Instance = this;
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += BrowserPage_Loaded;
        Unloaded += (_, _) =>
        {
            if (ReferenceEquals(Current, this)) Current = null;
            if (ReferenceEquals(Instance, this)) Instance = null;
        };
    }

    public void FocusAddressBar() { AddressBox.Focus(FocusState.Keyboard); AddressBox.SelectAll(); }
    public void Reload() { if (_isLoading) BrowserWebView.CoreWebView2?.Stop(); else BrowserWebView.CoreWebView2?.Reload(); }
    public void GoBack() { if (BrowserWebView.CoreWebView2?.CanGoBack == true) BrowserWebView.CoreWebView2.GoBack(); }
    public void GoForward() { if (BrowserWebView.CoreWebView2?.CanGoForward == true) BrowserWebView.CoreWebView2.GoForward(); }

    private async void BrowserPage_Loaded(object sender, RoutedEventArgs e)
    {
        Current = this;
        StartupDiagnostics.Info("BrowserPage.Loaded");
        StartupDiagnostics.Info("BrowserPage.InitializeRequested");
        _initializationTask ??= InitializeWebViewAsync();
        try
        {
            await _initializationTask;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("WEBVIEW_INITIALIZATION_UNOBSERVED", exception);
            ShowError("WEBVIEW_INITIALIZATION_FAILED", exception.Message);
            _initializationTask = null;
        }
    }

    internal static async Task VerifyEnvironmentAsync()
    {
        _ = await GetEnvironmentAsync();
        StartupDiagnostics.Info("WebView2.EnvironmentReady");
        StartupDiagnostics.Info("WebView2.Ready");
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip", "WebView2");
        try
        {
            Directory.CreateDirectory(profilePath);
            return await CoreWebView2Environment.CreateWithOptionsAsync(null, profilePath, null);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("The primary WebView2 profile could not be created; trying a temporary profile.", exception);
            var fallbackPath = Path.Combine(Path.GetTempPath(), "NovaClip", "WebView2", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(fallbackPath);
            return await CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackPath, null);
        }
    }

    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        lock (EnvironmentGate) return SharedEnvironmentTask ??= CreateEnvironmentAsync();
    }

    private static void ResetEnvironmentIfFailed(Task<CoreWebView2Environment> failedTask)
    {
        lock (EnvironmentGate)
        {
            if (ReferenceEquals(SharedEnvironmentTask, failedTask)) SharedEnvironmentTask = null;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            SetDetectionState(MediaDetectionState.Observing);
            var environmentTask = GetEnvironmentAsync();
            CoreWebView2Environment environment;
            try
            {
                environment = await environmentTask;
            }
            catch
            {
                ResetEnvironmentIfFailed(environmentTask);
                throw;
            }
            StartupDiagnostics.Info("WebView2.EnvironmentReady");
            StartupDiagnostics.Info("WebView2.Ready");
            StartupDiagnostics.Info("WebView2.ControlInitializing");
            await BrowserWebView.EnsureCoreWebView2Async(environment);
            StartupDiagnostics.Info("WebView2.ControlReady");
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
            StartupDiagnostics.Info("BrowserPage.Ready");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("WEBVIEW_INITIALIZATION_FAILED", exception);
            throw;
        }
    }

    private void Core_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) return;
        var decision = _policy.Evaluate(uri, BrowserNavigationKind.NewWindow);
        if (decision == BrowserNavigationDecision.NavigateInCurrentView) sender.Navigate(uri.ToString());
        else if (decision is BrowserNavigationDecision.OpenInSystemBrowser or BrowserNavigationDecision.AskUser) HandleExternalNavigation(uri);
        StartupDiagnostics.Info("Browser.NewWindowIntercepted");
    }

    private void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
        {
            args.Cancel = true;
            return;
        }
        var decision = _policy.Evaluate(uri, BrowserNavigationKind.Redirect);
        if (decision != BrowserNavigationDecision.NavigateInCurrentView)
        {
            args.Cancel = true;
            if (decision is BrowserNavigationDecision.OpenInSystemBrowser or BrowserNavigationDecision.AskUser) HandleExternalNavigation(uri);
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
        if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) ShowError("BROWSER_NAVIGATION_FAILED", args.WebErrorStatus.ToString());
    }

    private void Core_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args) => DispatcherQueue.TryEnqueue(() => AddressBox.Text = sender.Source);
    private void Core_HistoryChanged(CoreWebView2 sender, object args) => DispatcherQueue.TryEnqueue(() => { BackButton.IsEnabled = sender.CanGoBack; ForwardButton.IsEnabled = sender.CanGoForward; });
    private void Core_DocumentTitleChanged(CoreWebView2 sender, object args) => StartupDiagnostics.Info("Browser.DocumentTitleChanged");
    private void Core_ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) => DispatcherQueue.TryEnqueue(() => ShowWebViewFailure(args.ProcessFailedKind.ToString()));

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!BilibiliBridgeMessageParser.TryParse(args.WebMessageAsJson, out var message) || message is null) return;
        if (message.Type != BilibiliBridgeMessageType.PageContextChanged || !BilibiliBridgeMessageParser.TryReadPageContext(message, out var context)) return;
        if (context is null || !IsCurrentPageContext(context.Url)) return;
        _pageContext = context;
        DispatcherQueue.TryEnqueue(() => { TitleText.Text = context!.Title; IdentityText.Text = context.Bvid ?? string.Empty; SetDetectionState(MediaDetectionState.Observing); });
    }

    private async void Core_WebResourceResponseReceived(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var responseUri) || !BrowserNavigationPolicy.IsBilibiliHost(responseUri.Host) || !responseUri.AbsolutePath.Contains("/playurl", StringComparison.OrdinalIgnoreCase)) return;
        var generation = _navigationGeneration;
        try
        {
            using var stream = await args.Response.GetContentAsync();
            var json = await ReadBoundedTextAsync(stream.AsStreamForRead(), MaxPlayUrlResponseCharacters);
            if (json is null || generation != _navigationGeneration) return;
            var context = _pageContext is not null
                ? new PlayUrlContext(_pageContext.Url, _pageContext.Title, _pageContext.Bvid, _pageContext.Aid, _pageContext.Cid, _pageContext.EpisodeId, _pageContext.EpisodeTitle, _pageContext.Kind.Equals("bangumi", StringComparison.OrdinalIgnoreCase), ResolverStrategy.PlayUrlResponse)
                : new PlayUrlContext(sender.Source, _text.GetString("Browser_DefaultMediaTitle"));
            var result = _normalizer.Normalize(json, context);
            if (generation == _navigationGeneration) DispatcherQueue.TryEnqueue(() => ApplyResolveResult(result));
        }
        catch (OperationCanceledException)
        {
            // WebView2 can cancel an in-flight response while navigating or closing.
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
            var requestHeaders = await CreateMediaRequestHeadersAsync(_currentMedia.PageUrl);
            await AppServices.Downloads.EnqueueAsync(new DownloadRequest(Guid.NewGuid(), _currentMedia, video, audio, AppServices.Settings.DownloadDirectory, Path.GetFileName(outputFile), new RetryPolicy(AppServices.Settings.MaxRetryAttempts), AppServices.Settings.MergeAfterDownload, AppServices.Settings.DeleteTemporaryFilesAfterMerge, requestHeaders));
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
    private async void InfoActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewRecoveryRequested)
        {
            _webViewRecoveryRequested = false;
            InfoActionButton.Visibility = Visibility.Collapsed;
            await RecoverWebViewAsync();
            return;
        }
        if (_pendingExternalUri is null) return;
        var uri = _pendingExternalUri;
        _pendingExternalUri = null;
        BrowserInfoBar.IsOpen = false;
        try { await global::Windows.System.Launcher.LaunchUriAsync(uri); }
        catch (Exception exception) { ShowError("BROWSER_EXTERNAL_LAUNCH_FAILED", exception.Message); }
    }

    private void SetLoading(bool value) { _isLoading = value; RefreshButton.Content = new SymbolIcon(value ? Symbol.Cancel : Symbol.Refresh); }
    private void SetDetectionState(MediaDetectionState state)
    {
        DetectionProgress.IsActive = state is MediaDetectionState.Resolving or MediaDetectionState.Observing;
        DetectionText.Text = _text.GetString(state switch { MediaDetectionState.Ready => "Detection_Ready", MediaDetectionState.Resolving or MediaDetectionState.Observing => "Detection_Observing", MediaDetectionState.PermissionDenied => "Detection_PermissionDenied", MediaDetectionState.Error => "Detection_Error", _ => "Detection_Empty" });
        MediaDetails.Visibility = state == MediaDetectionState.Ready ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PresentExternalNavigation(Uri uri) { _webViewRecoveryRequested = false; _pendingExternalUri = uri; BrowserInfoBar.Severity = InfoBarSeverity.Informational; BrowserInfoBar.Message = _text.GetString("Browser_ExternalBlocked"); BrowserInfoBar.IsOpen = true; InfoActionButton.Visibility = Visibility.Visible; InfoActionButton.Content = _text.GetString("Browser_OpenExternal"); }
    private void ShowInfo(string message) { _webViewRecoveryRequested = false; BrowserInfoBar.Severity = InfoBarSeverity.Success; BrowserInfoBar.Message = message; BrowserInfoBar.IsOpen = true; InfoActionButton.Visibility = Visibility.Collapsed; }
    private void ShowError(string code, string? detail) { _webViewRecoveryRequested = false; BrowserInfoBar.Severity = InfoBarSeverity.Error; BrowserInfoBar.Message = _text.Format("Error_WithCode", code); BrowserInfoBar.IsOpen = true; InfoActionButton.Visibility = Visibility.Collapsed; StartupDiagnostics.Warning($"{code}: {detail}"); }
    private void ShowWebViewFailure(string detail)
    {
        _pendingExternalUri = null;
        _webViewRecoveryRequested = true;
        BrowserInfoBar.Severity = InfoBarSeverity.Error;
        BrowserInfoBar.Message = _text.Format("Error_WithCode", "WEBVIEW_PROCESS_FAILED");
        BrowserInfoBar.IsOpen = true;
        InfoActionButton.Content = _text.GetString("Browser_RetryWebView");
        InfoActionButton.Visibility = Visibility.Visible;
        StartupDiagnostics.Warning($"WEBVIEW_PROCESS_FAILED: {detail}");
    }

    private async Task RecoverWebViewAsync()
    {
        try
        {
            if (BrowserWebView.CoreWebView2 is { } core)
            {
                core.Navigate(_home.HomeUri.ToString());
            }
            else
            {
                _initializationTask = null;
                await InitializeWebViewAsync();
            }
            BrowserInfoBar.IsOpen = false;
            StartupDiagnostics.Info("WebView2.RecoveryRequested");
        }
        catch (Exception exception)
        {
            ShowError("WEBVIEW_RECOVERY_FAILED", exception.Message);
        }
    }

    public async Task ClearSessionAsync()
    {
        var core = BrowserWebView.CoreWebView2;
        if (core is null) return;
        core.CookieManager.DeleteAllCookies();
        await Task.CompletedTask;
        StartupDiagnostics.Info("Browser.SessionCleared");
    }

    public void ResetDetector()
    {
        _pageContext = null;
        _currentMedia = null;
        _videoTracks.Clear();
        SetDetectionState(MediaDetectionState.Observing);
        StartupDiagnostics.Info("MediaDetection.Reset");
    }

    private void HandleExternalNavigation(Uri uri)
    {
        if (AppServices.Settings.ExternalLinkBehavior.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            _ = LaunchExternalAsync(uri);
        }
        else
        {
            PresentExternalNavigation(uri);
        }
    }

    private async Task LaunchExternalAsync(Uri uri)
    {
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(uri)) ShowError("BROWSER_EXTERNAL_LAUNCH_FAILED", uri.Host);
        }
        catch (Exception exception)
        {
            ShowError("BROWSER_EXTERNAL_LAUNCH_FAILED", exception.Message);
        }
    }

    private bool IsCurrentPageContext(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var contextUri) || !Uri.TryCreate(BrowserWebView.Source?.ToString(), UriKind.Absolute, out var currentUri)) return false;
        return BrowserNavigationPolicy.IsBilibiliHost(contextUri.Host) &&
            contextUri.Scheme.Equals(currentUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            contextUri.Host.Equals(currentUri.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(contextUri.AbsolutePath, currentUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MediaRequestHeaders?> CreateMediaRequestHeadersAsync(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)) return null;
        var core = BrowserWebView.CoreWebView2;
        if (core is null) return new MediaRequestHeaders(Referer: pageUri.ToString(), Origin: pageUri.GetLeftPart(UriPartial.Authority), RefreshUrl: pageUri.ToString());

        string? cookieHeader = null;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(pageUri.ToString());
            var builder = new StringBuilder();
            foreach (var cookie in cookies)
            {
                if (string.IsNullOrWhiteSpace(cookie.Name) || cookie.Name.IndexOfAny(['\r', '\n', ';', '=']) >= 0 || cookie.Value.IndexOfAny(['\r', '\n', ';']) >= 0) continue;
                var separatorLength = builder.Length == 0 ? 0 : 2;
                if (builder.Length + separatorLength + cookie.Name.Length + 1 + cookie.Value.Length > MaxCookieHeaderCharacters) break;
                if (builder.Length > 0) builder.Append("; ");
                builder.Append(cookie.Name).Append('=').Append(cookie.Value);
            }
            cookieHeader = builder.Length == 0 ? null : builder.ToString();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Could not read WebView2 cookies for the media request.", exception);
        }

        string? userAgent = null;
        try
        {
            var raw = await core.ExecuteScriptAsync("navigator.userAgent");
            userAgent = JsonSerializer.Deserialize<string>(raw);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Could not read WebView2 user agent for the media request.", exception);
        }

        return new MediaRequestHeaders(pageUri.ToString(), pageUri.GetLeftPart(UriPartial.Authority), userAgent, cookieHeader, pageUri.ToString());
    }

    private static async Task<string?> ReadBoundedTextAsync(Stream stream, int maxCharacters)
    {
        using var reader = new StreamReader(stream);
        var builder = new StringBuilder(Math.Min(maxCharacters, 128 * 1024));
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if (builder.Length > maxCharacters - read) return null;
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }
    private static string QualityName(int? id) => id switch { 127 => "8K", 126 => "Dolby Vision", 125 => "HDR", 120 => "4K", 116 => "1080P60", 112 => "1080P+", 80 => "1080P", 64 => "720P", 32 => "480P", 16 => "360P", _ => "Auto" };
    private static string FormatBytes(long? bytes) => bytes is null or <= 0 ? "—" : bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):F1} GB" : $"{bytes / (1024d * 1024):F0} MB";
}
