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
using Microsoft.Web.WebView2.Core;

namespace NovaClip.App.Pages;

public sealed partial class BrowserPage : Page, IDisposable
{
    private const int MaxPlayUrlResponseCharacters = 10_000_000;
    private readonly BilibiliUrlResolver _urlResolver = new();
    private readonly BrowserNavigationPolicy _policy = new();
    private readonly BrowserHomeService _home = new();
    private readonly LocalizationService _text = new();
    private readonly BrowserMediaDetectionSession _mediaSession = new();
    private MediaDetectionCoordinator _detector => _mediaSession.Detector;
    private string? _bridgeDocumentId;
    private bool _snapshotQueued;
    private MediaDescriptor? _displayedMedia;
    private MediaDetectionState? _displayedState;
    private CancellationTokenSource? _settlementCancellation;
    private bool _closed;

    private Uri? _pendingExternalUri;
    private Task? _externalLaunchTask;
    private bool _webViewRecoveryRequested;
    private Task? _initializationTask;
    private Uri? _pendingNavigationUri;
    private bool _isLoading;

    public static BrowserPage? Current { get; private set; }
    public static BrowserPage? Instance { get; private set; }
    public bool HasInitializedWebView => BrowserWebView.CoreWebView2 is not null;

    public BrowserPage()
    {
        InitializeComponent();
        Instance = this;
        _detector.StateChanged += Detector_StateChanged;
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += BrowserPage_Loaded;
        Unloaded += BrowserPage_Unloaded;
    }

    public void FocusAddressBar()
    {
        AddressBox.Focus(FocusState.Keyboard);
        AddressBox.SelectAll();
    }

    public void NavigateAddress(string input)
    {
        if (!_urlResolver.TryResolve(input, out var uri))
        {
            ShowError("BROWSER_INVALID_ADDRESS", null);
            FocusAddressBar();
            return;
        }

        Navigate(uri);
    }

    public void Reload()
    {
        if (_isLoading) BrowserWebView.CoreWebView2?.Stop();
        else BrowserWebView.CoreWebView2?.Reload();
    }

    public void GoBack()
    {
        if (BrowserWebView.CoreWebView2?.CanGoBack == true) BrowserWebView.CoreWebView2.GoBack();
    }

    public void GoForward()
    {
        if (BrowserWebView.CoreWebView2?.CanGoForward == true) BrowserWebView.CoreWebView2.GoForward();
    }

    private async void BrowserPage_Loaded(object sender, RoutedEventArgs e)
    {
        Current = this;
        Instance = this;
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

    private void BrowserPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(Current, this)) Current = null;
        // The navigation cache still owns this page and its live WebView.
        // Settings and external activation must retain access while it is hidden.
    }

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
            if (_closed) return;
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

        _bridgeDocumentId = null;
        _mediaSession.BeginNavigation(uri);
        ScheduleDetectionSettlement();
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
            _ = ProbePageMediaAsync(sender);
        }
        else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            ShowError("BROWSER_NAVIGATION_FAILED", args.WebErrorStatus.ToString());
        }
    }

    private static void PersistLastPage(string? source)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOVACLIP_MEDIA_ACCEPTANCE_URL")) ||
            !AppServices.IsInitialized ||
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

    private void Core_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (_closed) return;
        if (AddressBox.Text != sender.Source) AddressBox.Text = sender.Source;
        if (!Uri.TryCreate(sender.Source, UriKind.Absolute, out var uri) ||
            !BrowserNavigationPolicy.IsBilibiliHost(uri.Host)) return;
        var previousGeneration = _detector.Snapshot.Page?.NavigationGeneration;
        var generation = _mediaSession.UpdatePageContext(new PageIdentity(sender.Source, null, null, null, null, 0));
        if (generation != previousGeneration)
        {
            ScheduleDetectionSettlement();
            _ = ProbePageMediaAsync(sender);
        }
    }

    private void Core_HistoryChanged(CoreWebView2 sender, object args) =>
        UpdateHistoryButtons(sender);

    private void UpdateHistoryButtons(CoreWebView2 sender)
    {
        if (!_closed)
        {
            BackButton.IsEnabled = sender.CanGoBack;
            ForwardButton.IsEnabled = sender.CanGoForward;
        }
    }

    private void Core_DocumentTitleChanged(CoreWebView2 sender, object args) =>
        StartupDiagnostics.Debug("Browser.DocumentTitleChanged");

    private void Core_ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => ShowWebViewFailure(args.ProcessFailedKind.ToString()));

    private void Detector_StateChanged(object? sender, MediaDetectionSnapshot snapshot)
    {
        lock (_mediaSession)
        {
            if (_snapshotQueued || _closed) return;
            _snapshotQueued = true;
        }

        if (!DispatcherQueue.TryEnqueue(() =>
            {
                lock (_mediaSession)
                {
                    _snapshotQueued = false;
                }
                // Render the latest authoritative state, including resets. A queued
                // intermediate candidate or older result cannot overwrite a newer one.
                if (!_closed) ApplyDetectionSnapshot(_detector.Snapshot);
            }))
        {
            lock (_mediaSession) _snapshotQueued = false;
        }
    }

    private async void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_closed || !IsCurrentPageContext(args.Source)) return;
        var generation = _detector.Snapshot.Page?.NavigationGeneration ?? 0;
        var json = args.WebMessageAsJson;
        if (json.Length > 2_000_000) return;
        try
        {
            // Context messages are small; playback payload parsing and cloning can
            // be large, so it runs on a worker. WebView APIs remain on this thread.
            BilibiliBridgeMessage? message;
            if (json.Length < 16_000) message = ParseBridgeMessage(json);
            else
            {
                using var parseLease = _mediaSession.TryBeginObservation(generation, isPageData: true);
                if (parseLease is null) return;
                message = await Task.Run(() => ParseBridgeMessage(json), parseLease.CancellationToken);
            }
            if (_closed || message is null || _detector.Snapshot.Page?.NavigationGeneration != generation) return;
            if (message.Type == BilibiliBridgeMessageType.BridgeReady)
            {
                if (ReadString(message.Payload, "url") is { } url && IsCurrentPageContext(url))
                    await ProbePageMediaAsync(sender);
                return;
            }

            if (_bridgeDocumentId is null ||
                !string.Equals(ReadString(message.Payload, "documentId"), _bridgeDocumentId, StringComparison.Ordinal) ||
                !BilibiliBridgeMessageParser.TryReadPageContext(message, out var context) ||
                context is null || !IsCurrentPageContext(context.Url)) return;

            var page = new PageIdentity(context.Url, context.Bvid, context.Aid, context.Cid,
                context.EpisodeId, generation, context.Title, context.EpisodeTitle,
                context.Kind.Equals("bangumi", StringComparison.OrdinalIgnoreCase), context.Page);
            var updatedGeneration = _mediaSession.UpdatePageContext(page);
            page = page with { NavigationGeneration = updatedGeneration };
            if (updatedGeneration != generation) ScheduleDetectionSettlement();

            if (message.Type == BilibiliBridgeMessageType.DetectionCompleted)
            {
                if (ReadString(message.Payload, "errorCode") is { Length: > 0 } errorCode)
                    _detector.FailObservation(updatedGeneration, errorCode,
                        errorCode is "RESOLVE_LOGIN_REQUIRED" or "RESOLVE_VIP_REQUIRED"
                            ? MediaDetectionState.PermissionDenied : MediaDetectionState.Error);
                else _detector.CompleteObservation(updatedGeneration);
                return;
            }
            if (message.Type != BilibiliBridgeMessageType.HydrateDataFound ||
                !message.Payload.TryGetProperty("playUrl", out var playUrl) ||
                playUrl.ValueKind != JsonValueKind.Object) return;

            using var lease = _mediaSession.TryBeginObservation(updatedGeneration, isPageData: true);
            if (lease is null) return;
            await Task.Run(async () =>
            {
                lease.CancellationToken.ThrowIfCancellationRequested();
                var playUrlJson = playUrl.GetRawText();
                if (ReadString(message.Payload, "endpoint") is { } endpoint &&
                    Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
                    await _detector.ObserveAsync(new PlayUrlObservation(endpointUri, playUrlJson,
                        lease.Generation, DateTimeOffset.UtcNow), lease.CancellationToken);
                else
                    await _detector.ObservePageDataAsync(playUrlJson, page, lease.Generation, lease.CancellationToken);
            }, lease.CancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _detector.FailObservation(_detector.Snapshot.Page?.NavigationGeneration == generation ? generation : -1,
                "MEDIA_BRIDGE_FAILED");
            StartupDiagnostics.Warning("MEDIA_BRIDGE_FAILED", exception);
        }
    }

    private static BilibiliBridgeMessage? ParseBridgeMessage(string json) =>
        BilibiliBridgeMessageParser.TryParse(json, out var message) ? message : null;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

        if (_closed) return;
        var page = _detector.Snapshot.Page;
        var generation = page?.NavigationGeneration ?? 0;
        if (page is null || generation == 0) return;

        using var lease = _mediaSession.TryBeginObservation(generation);
        if (lease is null) return;
        try
        {
            using var stream = await AwaitResponseContentAsync(args.Response.GetContentAsync().AsTask(), lease.CancellationToken);
            if (stream is null) return;
            // GetContentAsync is a WebView call. Reading/normalizing its stream is
            // CPU and I/O work and must not resume on the WinUI synchronization context.
            await Task.Run(async () =>
            {
                var json = await ReadBoundedTextAsync(stream.AsStreamForRead(), MaxPlayUrlResponseCharacters,
                    lease.CancellationToken).ConfigureAwait(false);
                if (json is null)
                {
                    _detector.FailObservation(generation, "MEDIA_RESPONSE_TOO_LARGE");
                    return;
                }
                await _detector.ObserveAsync(new PlayUrlObservation(responseUri, json, generation,
                    DateTimeOffset.UtcNow), lease.CancellationToken).ConfigureAwait(false);
            }, lease.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // WebView2 can cancel an in-flight response while navigating or closing.
        }
        catch (Exception exception)
        {
            if (!_closed && _detector.Snapshot.Page?.NavigationGeneration == generation)
            {
                _detector.FailObservation(generation, "MEDIA_PLAYURL_READ_FAILED");
                StartupDiagnostics.Warning("MEDIA_PLAYURL_READ_FAILED", exception);
            }
        }
    }

    private void ApplyDetectionSnapshot(MediaDetectionSnapshot snapshot)
    {
        if (_displayedState != snapshot.State)
        {
            _displayedState = snapshot.State;
            SetDetectionState(snapshot.State);
            // No URLs or request credentials enter diagnostics. State/identity are
            // enough to determine where navigation results were discarded.
            StartupDiagnostics.Info($"MediaDetection.State:{snapshot.State};generation:{snapshot.Page?.NavigationGeneration};cid:{snapshot.Page?.Cid};code:{snapshot.ErrorCode}");
        }
        if (snapshot.Media is not MediaDescriptor media)
        {
            _displayedMedia = null;
            AddDownloadButton.IsEnabled = false;
            if (snapshot.State != MediaDetectionState.Ready) QualityCombo.Items.Clear();
            return;
        }
        if (ReferenceEquals(_displayedMedia, media)) return;
        var previousTracks = _displayedMedia?.Tracks.Where(track => track.Type == TrackType.Video).ToArray();
        var previousSelection = _displayedMedia?.Cid == media.Cid && previousTracks is not null &&
            QualityCombo.SelectedIndex >= 0 && QualityCombo.SelectedIndex < previousTracks.Length
                ? previousTracks[QualityCombo.SelectedIndex] : null;
        _displayedMedia = media;

        var videoTracks = media.Tracks.Where(track => track.Type == TrackType.Video).ToList();
        QualityCombo.Items.Clear();
        foreach (var track in videoTracks)
        {
            QualityCombo.Items.Add(
                QualityName(track.QualityId) + " · " +
                (track.Codec ?? "—") + " · " +
                FormatBytes(track.Size));
        }

        var preferred = previousSelection is null ? null : videoTracks.FirstOrDefault(track =>
            track.QualityId == previousSelection.QualityId && track.Codec == previousSelection.Codec);
        preferred ??= SelectVideoTrack(videoTracks);
        if (preferred is not null)
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

        var requested = quality.ToUpperInvariant() switch
        {
            "1080P" => 80, "720P" => 64, "480P" => 32, "360P" => 16,
            _ => int.TryParse(quality, CultureInfo.InvariantCulture, out var id) ? id : 0
        };
        if (requested > 0)
        {
            return filtered.OrderBy(track =>
                Math.Abs((track.QualityId ?? 0) - requested)).First();
        }

        return filtered[0];
    }

    private async void AddDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        AddDownloadButton.IsEnabled = false;
        try
        {
            if (await EnqueueCurrentMediaAsync() is not null) ShowInfo(_text.GetString("Download_Queued"));
        }
        catch (FfmpegUnavailableException exception) { ShowError("DOWNLOAD_FFMPEG_REQUIRED", exception.Message); }
        catch (Exception exception) { ShowError("DOWNLOAD_CREATE_FAILED", exception.Message); }
        finally { ApplyDownloadButtonState(); }
    }

    internal async Task<Guid?> EnqueueCurrentMediaAsync()
    {
        var snapshot = _detector.Snapshot;
        if (snapshot.State != MediaDetectionState.Ready || snapshot.Media is not MediaDescriptor media ||
            !ReferenceEquals(_displayedMedia, media)) return null;

        var videoTracks = media.Tracks.Where(track => track.Type == TrackType.Video).ToList();
        var video = videoTracks.Count == 0
            ? null
            : videoTracks[Math.Clamp(QualityCombo.SelectedIndex, 0, videoTracks.Count - 1)];
        var audio = media.Tracks.FirstOrDefault(track => track.Type == TrackType.Audio);
        if (video is null && audio is null && media.LegacySegments.Count == 0) return null;

            var title = AppServices.FileNames.Sanitize(media.Title, "Bilibili");
            var extension = video is null && audio is not null ? ".m4a" : ".mp4";
            var requestHeaders = await BrowserMediaRequestHeadersFactory.CreateAsync(BrowserWebView.CoreWebView2, media.PageUrl);
            // Cookie/UA collection awaits WebView. A switch during that await must
            // not silently queue the old card with the new page's session context.
            if (_closed || _detector.Snapshot.Page?.NavigationGeneration != snapshot.Page?.NavigationGeneration ||
                !ReferenceEquals(_detector.Snapshot.Media, media)) return null;
            return await AppServices.Downloads.EnqueueAsync(new DownloadRequest(
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
    }

    private void ApplyDownloadButtonState() => AddDownloadButton.IsEnabled = !_closed &&
        _detector.Snapshot.State == MediaDetectionState.Ready && _detector.Snapshot.Media is MediaDescriptor;

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
            MediaDetectionState.WaitingForPageContext => "Detection_Waiting",
            MediaDetectionState.Expired => "Detection_Expired",
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
        BrowserInfoBar.Message = code == "DOWNLOAD_FFMPEG_REQUIRED"
            ? _text.GetString("Download_FfmpegRequired") : _text.Format("Error_WithCode", code);
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
        // Do not delete a profile directory while WebView initialization owns it.
        if (_initializationTask is { } initialization) await initialization;
        var core = BrowserWebView.CoreWebView2;
        if (core is null) return;

        core.CookieManager.DeleteAllCookies();
        await Task.CompletedTask;
        StartupDiagnostics.Info("Browser.SessionCleared");
    }

    public void ResetDetector()
    {
        _mediaSession.Reset();
        if (Uri.TryCreate(BrowserWebView.CoreWebView2?.Source, UriKind.Absolute, out var source))
        {
            _mediaSession.BeginNavigation(source);
            ScheduleDetectionSettlement();
            _ = ProbePageMediaAsync(BrowserWebView.CoreWebView2!);
        }
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
            !Uri.TryCreate(BrowserWebView.CoreWebView2?.Source ?? BrowserWebView.Source?.ToString(), UriKind.Absolute, out var currentUri))
        {
            return false;
        }

        // Source may still name the outgoing document between NavigationStarting
        // and SourceChanged. Its messages must not turn the intended new page back
        // into that old video; the current URI and the navigation target both apply.
        var expectedUrl = _detector.Snapshot.Page?.PageUrl;
        return BrowserNavigationPolicy.IsBilibiliHost(contextUri.Host) &&
            BilibiliMediaIdentity.IsSamePage(contextUri, currentUri) &&
            (expectedUrl is null || Uri.TryCreate(expectedUrl, UriKind.Absolute, out var expectedUri) &&
                BilibiliMediaIdentity.IsSamePage(contextUri, expectedUri));
    }

    private static async Task<string?> ReadBoundedTextAsync(Stream stream, int maxCharacters, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var builder = new StringBuilder(Math.Min(maxCharacters, 128 * 1024));
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (builder.Length > maxCharacters - read) return null;
            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private async Task ProbePageMediaAsync(CoreWebView2 core)
    {
        var generation = _detector.Snapshot.Page?.NavigationGeneration;
        try
        {
            if (_closed) return;
            var documentJson = await core.ExecuteScriptAsync("window.__novaClipDocumentId || null;");
            var documentId = JsonSerializer.Deserialize<string>(documentJson);
            if (_closed || generation != _detector.Snapshot.Page?.NavigationGeneration || documentId is null) return;
            _bridgeDocumentId = documentId;
            await core.ExecuteScriptAsync("window.__novaClipProbeMedia && window.__novaClipProbeMedia(); void 0;");
        }
        catch (Exception exception)
        {
            if (!_closed && generation == _detector.Snapshot.Page?.NavigationGeneration)
                StartupDiagnostics.Warning("MEDIA_PAGE_PROBE_FAILED", exception);
        }
    }

    private static async Task<T> AwaitResponseContentAsync<T>(Task<T> contentTask, CancellationToken cancellationToken)
        where T : IDisposable
    {
        try { return await contentTask.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // WebView's COM read may finish after navigation cancellation. Release
            // our bounded reader immediately and dispose a late stream when it arrives.
            _ = DisposeLateResponseAsync(contentTask);
            throw;
        }
    }

    private static async Task DisposeLateResponseAsync<T>(Task<T> contentTask) where T : IDisposable
    {
        try { (await contentTask.ConfigureAwait(false))?.Dispose(); }
        catch (Exception exception) { StartupDiagnostics.Debug("MediaDetection.RetiredResponse:" + exception.GetType().Name); }
    }

    private void ScheduleDetectionSettlement()
    {
        _settlementCancellation?.Cancel();
        _settlementCancellation?.Dispose();
        _settlementCancellation = new CancellationTokenSource();
        _ = SettleDetectionAsync(_detector.Snapshot.Page?.NavigationGeneration ?? 0, _settlementCancellation.Token);
    }

    private async Task SettleDetectionAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            // One deadline bounds the observation window; this does not poll or
            // issue requests. Bridge events can still recover a later successful play.
            await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            if (_closed || _detector.Snapshot.Page?.NavigationGeneration != generation) return;
            _detector.CompleteObservation(generation);
            if (_detector.Snapshot.State == MediaDetectionState.WaitingForPageContext)
                _detector.FailObservation(generation, "MEDIA_PAGE_CONTEXT_TIMEOUT");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal MediaDetectionSnapshot AcceptanceSnapshot => _detector.Snapshot;
    internal bool IsPageBridgeReady => _bridgeDocumentId is not null;
    internal bool IsMediaCardReady => AddDownloadButton.IsEnabled && MediaDetails.Visibility == Visibility.Visible &&
        ReferenceEquals(_displayedMedia, _detector.Snapshot.Media);
    internal void SelectLowestAcceptanceQuality()
    {
        if (_detector.Snapshot.Media is not MediaDescriptor media) return;
        var tracks = media.Tracks.Where(track => track.Type == TrackType.Video).ToList();
        if (tracks.Count == 0) return;
        var lowest = tracks.OrderBy(track => track.QualityId ?? 0)
            .ThenBy(track => track.Size ?? long.MaxValue).First();
        QualityCombo.SelectedIndex = tracks.IndexOf(lowest);
    }
    internal Task<string> ExecuteAcceptanceScriptAsync(string script) =>
        BrowserWebView.CoreWebView2 is { } core ? core.ExecuteScriptAsync(script).AsTask() : Task.FromResult("null");

    internal void CloseBrowser()
    {
        if (_closed) return;
        _closed = true;
        _settlementCancellation?.Cancel();
        _settlementCancellation?.Dispose();
        _detector.StateChanged -= Detector_StateChanged;
        _mediaSession.Dispose();
        if (BrowserWebView.CoreWebView2 is { } core)
        {
            core.NewWindowRequested -= Core_NewWindowRequested;
            core.NavigationStarting -= Core_NavigationStarting;
            core.NavigationCompleted -= Core_NavigationCompleted;
            core.SourceChanged -= Core_SourceChanged;
            core.HistoryChanged -= Core_HistoryChanged;
            core.DocumentTitleChanged -= Core_DocumentTitleChanged;
            core.ProcessFailed -= Core_ProcessFailed;
            core.WebMessageReceived -= Core_WebMessageReceived;
            core.WebResourceResponseReceived -= Core_WebResourceResponseReceived;
        }
        BrowserWebView.Close();
        if (ReferenceEquals(Instance, this)) Instance = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    public void Dispose()
    {
        CloseBrowser();
        GC.SuppressFinalize(this);
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
