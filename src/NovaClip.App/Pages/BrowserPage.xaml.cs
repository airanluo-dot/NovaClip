using System.Globalization;
using System.Text;
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
    private const int MaxPlayUrlResponseCharacters = 10_000_000;
    private const int MaxCookieHeaderCharacters = 64_000;

    private readonly PlayUrlNormalizer _normalizer = new();
    private readonly BilibiliUrlResolver _urlResolver = new();
    private readonly BrowserNavigationPolicy _policy = new();
    private readonly BrowserHomeService _home = new();
    private readonly LocalizationService _text = new();
    private readonly MediaDetectionCoordinator _detector = new(Array.Empty<IMediaDetectionStrategy>());

    private Uri? _pendingExternalUri;
    private Task? _externalLaunchTask;
    private bool _webViewRecoveryRequested;
    private Task? _initializationTask;
    private Uri? _pendingNavigationUri;
    private bool _isLoading;

    internal static Task VerifyEnvironmentAsync() => BrowserWebViewEnvironment.VerifyAsync();

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environmentTask = BrowserWebViewEnvironment.GetAsync();
            CoreWebView2Environment environment;
            try
            {
                environment = await environmentTask;
            }
            catch
            {
                BrowserWebViewEnvironment.ResetIfFailed(environmentTask);
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
            if (File.Exists(bridgePath))
            {
                var bridge = await File.ReadAllTextAsync(bridgePath);
                await core.AddScriptToExecuteOnDocumentCreatedAsync(bridge);
            }

            var initialUri = _pendingNavigationUri ?? (
                string.Equals(AppServices.Settings.BrowserStartup, "LastPage", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(AppServices.Settings.LastBrowserUrl, UriKind.Absolute, out var lastUri) &&
                _policy.Evaluate(lastUri, BrowserNavigationKind.User) == BrowserNavigationDecision.NavigateInCurrentView
                    ? lastUri
                    : _home.HomeUri);
            _pendingNavigationUri = null;
            Navigate(initialUri);
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
        if (decision == BrowserNavigationDecision.NavigateInCurrentView)
        {
            sender.Navigate(uri.ToString());
        }
        else if (decision is BrowserNavigationDecision.OpenInSystemBrowser or BrowserNavigationDecision.AskUser)
        {
            HandleExternalNavigation(uri);
        }

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
            if (decision is BrowserNavigationDecision.OpenInSystemBrowser or BrowserNavigationDecision.AskUser)
            {
                HandleExternalNavigation(uri);
            }

            return;
        }

        _detector.BeginNavigation(uri);
        SetLoading(true);
        SetDetectionState(MediaDetectionState.WaitingForPageContext);
        StartupDiagnostics.Info("Browser.NavigationStarted");
    }

    private void Core_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        SetLoading(false);
        if (args.IsSuccess)
        {
            PersistLastPage(sender.Source);
        }
        else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            ShowError("BROWSER_NAVIGATION_FAILED", args.WebErrorStatus.ToString());
        }
    }

    private static void PersistLastPage(string? source)
    {
        if (!AppServices.IsInitialized ||
            !Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            !BrowserNavigationPolicy.IsBilibiliHost(uri.Host) ||
            uri.Scheme is not ("http" or "https") ||
            string.Equals(AppServices.Settings.LastBrowserUrl, source, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            AppServices.SettingsCoordinator.Apply(settings => settings.LastBrowserUrl = source);
            StartupDiagnostics.Info("Browser.LastPagePersisted");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Could not persist the last browser page.", exception);
        }
    }

    private void Core_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => AddressBox.Text = sender.Source);

    private void Core_HistoryChanged(CoreWebView2 sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            BackButton.IsEnabled = sender.CanGoBack;
            ForwardButton.IsEnabled = sender.CanGoForward;
        });

    private void Core_DocumentTitleChanged(CoreWebView2 sender, object args) =>
        StartupDiagnostics.Info("Browser.DocumentTitleChanged");

    private void Core_ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => ShowWebViewFailure(args.ProcessFailedKind.ToString()));

    private void Detector_StateChanged(object? sender, MediaDetectionSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var currentGeneration = _detector.Snapshot.Page?.NavigationGeneration ?? 0;
            var snapshotGeneration = snapshot.Page?.NavigationGeneration ?? 0;
            if (snapshotGeneration != 0 && currentGeneration != 0 && snapshotGeneration < currentGeneration) return;
            ApplyDetectionSnapshot(snapshot);
        });
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!BilibiliBridgeMessageParser.TryParse(args.WebMessageAsJson, out var message) ||
            message is null ||
            message.Type != BilibiliBridgeMessageType.PageContextChanged ||
            !BilibiliBridgeMessageParser.TryReadPageContext(message, out var context) ||
            context is null ||
            !IsCurrentPageContext(context.Url))
        {
            return;
        }

        var page = new PageIdentity(
            context.Url,
            context.Bvid,
            context.Aid,
            context.Cid,
            context.EpisodeId,
            0,
            context.Title,
            context.EpisodeTitle,
            context.Kind.Equals("bangumi", StringComparison.OrdinalIgnoreCase));
        _detector.UpdatePageContext(page);
        StartupDiagnostics.Info("MediaDetection.PageContextAccepted");
    }

    private async void Core_WebResourceResponseReceived(
        CoreWebView2 sender,
        CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var responseUri) ||
            !BrowserNavigationPolicy.IsBilibiliHost(responseUri.Host) ||
            !responseUri.AbsolutePath.Contains("/playurl", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var page = _detector.Snapshot.Page;
        var generation = page?.NavigationGeneration ?? 0;
        if (page is null || generation == 0) return;

        try
        {
            using var stream = await args.Response.GetContentAsync();
            if (stream is null) return;

            var json = await ReadBoundedTextAsync(stream.AsStreamForRead(), MaxPlayUrlResponseCharacters);
            if (json is null) return;
            if (_detector.Snapshot.Page?.NavigationGeneration != generation) return;

            page = _detector.Snapshot.Page;
            if (page is null || page.NavigationGeneration != generation) return;

            var context = new PlayUrlContext(
                page.PageUrl,
                page.Title ?? _text.GetString("Browser_DefaultMediaTitle"),
                page.Bvid,
                page.Aid,
                page.Cid,
                page.EpisodeId,
                page.EpisodeTitle,
                page.IsBangumi,
                ResolverStrategy.PlayUrlResponse);
            var result = _normalizer.Normalize(json, context);
            var media = result.Media;
            var track = media?.VideoTrack ?? media?.AudioTrack;
            var fingerprint = new MediaFingerprint(
                page.PageUrl,
                page.Bvid,
                page.Aid,
                page.Cid,
                page.EpisodeId,
                track?.QualityId,
                track?.Codec,
                generation);
            var detectionResult = new MediaDetectionResult(
                result.IsSuccess,
                result.IsSuccess ? MediaDetectionState.Ready : MediaDetectionState.Error,
                fingerprint,
                media,
                result.Error?.Code);
            _detector.TryAcceptResult(generation, detectionResult);
        }
        catch (OperationCanceledException)
        {
            // WebView2 can cancel an in-flight response while navigating or closing.
        }
        catch (Exception exception)
        {
            if (_detector.Snapshot.Page?.NavigationGeneration == generation)
            {
                ShowError("MEDIA_PLAYURL_READ_FAILED", exception.Message);
            }
        }
    }

    private void ApplyDetectionSnapshot(MediaDetectionSnapshot snapshot)
    {
        SetDetectionState(snapshot.State);
        if (snapshot.Media is not MediaDescriptor media)
        {
            AddDownloadButton.IsEnabled = false;
            if (snapshot.State != MediaDetectionState.Ready) QualityCombo.Items.Clear();
            return;
        }

        var videoTracks = media.Tracks.Where(track => track.Type == TrackType.Video).ToList();
        QualityCombo.Items.Clear();
        foreach (var track in videoTracks)
        {
            QualityCombo.Items.Add(
                QualityName(track.QualityId) + " · " +
                (track.Codec ?? "—") + " · " +
                FormatBytes(track.Size));
        }

        if (videoTracks.Count > 0 && SelectVideoTrack(videoTracks) is { } preferred)
        {
            QualityCombo.SelectedIndex = videoTracks.IndexOf(preferred);
        }

        TitleText.Text = media.Title;
        IdentityText.Text = media.Bvid ??
            media.EpisodeId?.ToString(CultureInfo.InvariantCulture) ??
            string.Empty;
        TrackText.Text = videoTracks.Count == 0 && media.LegacySegments.Count > 0
            ? "DURL · " + media.LegacySegments.Count.ToString(CultureInfo.InvariantCulture) + " segments"
            : (media.VideoTrack?.Codec ?? "—") + " · " + (media.AudioTrack?.Codec ?? "—");
        AddDownloadButton.IsEnabled =
            videoTracks.Count > 0 ||
            media.AudioTrack is not null ||
            media.LegacySegments.Count > 0;
        MediaDetails.Visibility = Visibility.Visible;
        StartupDiagnostics.Info("MediaDetection.Ready");
    }

    private static MediaTrack? SelectVideoTrack(List<MediaTrack> tracks)
    {
        if (tracks.Count == 0) return null;

        var codec = AppServices.Settings.DefaultCodec;
        IReadOnlyList<MediaTrack> filtered = codec.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? tracks
            : tracks.Where(track =>
                (track.Codec ?? string.Empty).Contains(codec, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filtered.Count == 0) filtered = tracks;

        var quality = AppServices.Settings.DefaultQuality;
        if (quality.Equals("Highest", StringComparison.OrdinalIgnoreCase))
        {
            return filtered.OrderByDescending(track => track.QualityId ?? 0).First();
        }

        if (int.TryParse(quality.TrimEnd('P', 'p'), out var requested))
        {
            return filtered.OrderBy(track =>
                Math.Abs((track.QualityId ?? 0) - requested)).First();
        }

        return filtered[0];
    }

    private async void AddDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detector.Snapshot.Media is not MediaDescriptor media) return;

        var videoTracks = media.Tracks.Where(track => track.Type == TrackType.Video).ToList();
        var video = videoTracks.Count == 0
            ? null
            : videoTracks[Math.Clamp(QualityCombo.SelectedIndex, 0, videoTracks.Count - 1)];
        var audio = media.Tracks.FirstOrDefault(track => track.Type == TrackType.Audio);
        if (video is null && audio is null && media.LegacySegments.Count == 0) return;

        try
        {
            var title = AppServices.FileNames.Sanitize(media.Title, "Bilibili");
            var extension = video is null && audio is not null ? ".m4a" : ".mp4";
            var requestHeaders = await BrowserMediaRequestHeadersFactory.CreateAsync(BrowserWebView.CoreWebView2, media.PageUrl);
            await AppServices.Downloads.EnqueueAsync(new DownloadRequest(
                Guid.NewGuid(),
                media,
                video,
                audio,
                AppServices.Settings.DownloadDirectory,
                title + extension,
                new RetryPolicy(AppServices.Settings.MaxRetryAttempts),
                AppServices.Settings.MergeAfterDownload,
                AppServices.Settings.DeleteTemporaryFilesAfterMerge,
                requestHeaders));
            ShowInfo(_text.GetString("Download_Queued"));
        }
        catch (Exception exception)
        {
            ShowError("DOWNLOAD_CREATE_FAILED", exception.Message);
        }
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter &&
            _urlResolver.TryResolve(AddressBox.Text, out var uri))
        {
            Navigate(uri);
            e.Handled = true;
        }
    }

    private void Navigate(Uri uri)
    {
        if (BrowserWebView.CoreWebView2 is null)
        {
            _pendingNavigationUri = uri;
            return;
        }

        if (_policy.Evaluate(uri, BrowserNavigationKind.AddressBar) != BrowserNavigationDecision.NavigateInCurrentView)
        {
            ShowError("BROWSER_NAVIGATION_BLOCKED", uri.Host);
            return;
        }

        BrowserWebView.CoreWebView2.Navigate(uri.ToString());
    }
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
        try
        {
            await global::Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception exception)
        {
            ShowError("BROWSER_EXTERNAL_LAUNCH_FAILED", exception.Message);
        }
    }

    private void SetLoading(bool value)
    {
        _isLoading = value;
        RefreshButton.Content = new SymbolIcon(value ? Symbol.Cancel : Symbol.Refresh);
    }

    private void SetDetectionState(MediaDetectionState state)
    {
        DetectionProgress.IsActive =
            state is MediaDetectionState.Resolving or MediaDetectionState.Observing;
        DetectionText.Text = _text.GetString(state switch
        {
            MediaDetectionState.Ready => "Detection_Ready",
            MediaDetectionState.Resolving or MediaDetectionState.Observing => "Detection_Observing",
            MediaDetectionState.PermissionDenied => "Detection_PermissionDenied",
            MediaDetectionState.Error => "Detection_Error",
            _ => "Detection_Empty"
        });
        MediaDetails.Visibility = state == MediaDetectionState.Ready
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PresentExternalNavigation(Uri uri)
    {
        _webViewRecoveryRequested = false;
        _pendingExternalUri = uri;
        BrowserInfoBar.Severity = InfoBarSeverity.Informational;
        BrowserInfoBar.Message = _text.GetString("Browser_ExternalBlocked");
        BrowserInfoBar.IsOpen = true;
        InfoActionButton.Visibility = Visibility.Visible;
        InfoActionButton.Content = _text.GetString("Browser_OpenExternal");
    }

    private void ShowInfo(string message)
    {
        _webViewRecoveryRequested = false;
        BrowserInfoBar.Severity = InfoBarSeverity.Success;
        BrowserInfoBar.Message = message;
        BrowserInfoBar.IsOpen = true;
        InfoActionButton.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string code, string? detail)
    {
        _webViewRecoveryRequested = false;
        BrowserInfoBar.Severity = InfoBarSeverity.Error;
        BrowserInfoBar.Message = _text.Format("Error_WithCode", code);
        BrowserInfoBar.IsOpen = true;
        InfoActionButton.Visibility = Visibility.Collapsed;
        StartupDiagnostics.Warning(code + ": " + detail);
    }

    private void ShowWebViewFailure(string detail)
    {
        _pendingExternalUri = null;
        _webViewRecoveryRequested = true;
        BrowserInfoBar.Severity = InfoBarSeverity.Error;
        BrowserInfoBar.Message = _text.Format("Error_WithCode", "WEBVIEW_PROCESS_FAILED");
        BrowserInfoBar.IsOpen = true;
        InfoActionButton.Content = _text.GetString("Browser_RetryWebView");
        InfoActionButton.Visibility = Visibility.Visible;
        StartupDiagnostics.Warning("WEBVIEW_PROCESS_FAILED: " + detail);
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
        _detector.Reset();
        SetDetectionState(MediaDetectionState.Observing);
        StartupDiagnostics.Info("MediaDetection.Reset");
    }

    private void HandleExternalNavigation(Uri uri)
    {
        if (AppServices.Settings.ExternalLinkBehavior.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            _externalLaunchTask = LaunchExternalAsync(uri);
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
            if (!await global::Windows.System.Launcher.LaunchUriAsync(uri))
            {
                ShowError("BROWSER_EXTERNAL_LAUNCH_FAILED", uri.Host);
            }
        }
        catch (Exception exception)
        {
            ShowError("BROWSER_EXTERNAL_LAUNCH_FAILED", exception.Message);
        }
    }

    private bool IsCurrentPageContext(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var contextUri) ||
            !Uri.TryCreate(BrowserWebView.Source?.ToString(), UriKind.Absolute, out var currentUri))
        {
            return false;
        }

        return BrowserNavigationPolicy.IsBilibiliHost(contextUri.Host) &&
            contextUri.Scheme.Equals(currentUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            contextUri.Host.Equals(currentUri.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(contextUri.AbsolutePath, currentUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
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

    private static string QualityName(int? id) => id switch
    {
        127 => "8K",
        126 => "Dolby Vision",
        125 => "HDR",
        120 => "4K",
        116 => "1080P60",
        112 => "1080P+",
        80 => "1080P",
        64 => "720P",
        32 => "480P",
        16 => "360P",
        _ => "Auto"
    };

    private static string FormatBytes(long? bytes) =>
        bytes is null or <= 0
            ? "—"
            : bytes >= 1024L * 1024 * 1024
                ? $"{bytes / (1024d * 1024 * 1024):F1} GB"
                : $"{bytes / (1024d * 1024):F0} MB";
}
