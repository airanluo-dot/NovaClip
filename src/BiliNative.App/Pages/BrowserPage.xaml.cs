using System.Globalization;
using System.Text.Json;
using BiliNative.Core;
using BiliNative.WebBridge;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace BiliNative.App.Pages;

public sealed partial class BrowserPage : Page
{
    private readonly PlayUrlNormalizer _normalizer = new();
    private BilibiliPageContext? _pageContext;
    private MediaDescriptor? _currentMedia;
    private List<MediaTrack> _videoTracks = [];
    private string? _lastPlayUrlUri;
    private bool _initialized;

    public BrowserPage()
    {
        InitializeComponent();
        Loaded += BrowserPage_Loaded;
    }

    private async void BrowserPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip", "WebView2");
            Directory.CreateDirectory(profilePath);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profilePath, null);
            await BrowserWebView.EnsureCoreWebView2Async(environment);
            var core = BrowserWebView.CoreWebView2;
            core.WebMessageReceived += Core_WebMessageReceived;
            core.WebResourceResponseReceived += Core_WebResourceResponseReceived;
            core.SourceChanged += Core_SourceChanged;
            var bridgePath = Path.Combine(AppContext.BaseDirectory, "assets", "js", "bilibili-bridge.js");
            if (File.Exists(bridgePath)) await core.AddScriptToExecuteOnDocumentCreatedAsync(await File.ReadAllTextAsync(bridgePath));
            AddressBox.Text = "https://www.bilibili.com/";
            BrowserWebView.Source = new Uri(AddressBox.Text);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("WebView2 initialization failed.", exception);
            StatusText.Text = $"WebView2 初始化失败：{exception.Message}";
        }
    }

    private void Core_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => AddressBox.Text = sender.Source);
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!BilibiliBridgeMessageParser.TryParse(args.WebMessageAsJson, out var message) || message is null) return;
        if (message.Type == BilibiliBridgeMessageType.PageContextChanged && BilibiliBridgeMessageParser.TryReadPageContext(message, out var context))
        {
            _pageContext = context;
            _lastPlayUrlUri = null;
            DispatcherQueue.TryEnqueue(() =>
            {
                _currentMedia = null;
                _videoTracks.Clear();
                QualityCombo.Items.Clear();
                AddDownloadButton.IsEnabled = false;
                TitleText.Text = context!.Title;
                IdentityText.Text = $"{context.Bvid ?? "未识别 BV"} · CID {context.Cid?.ToString(CultureInfo.InvariantCulture) ?? "等待播放"}";
                TrackText.Text = "等待播放器返回 PlayURL…";
            });
        }
    }

    private async void Core_WebResourceResponseReceived(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        var requestUri = args.Request.Uri;
        if (!requestUri.Contains("/playurl", StringComparison.OrdinalIgnoreCase)) return;
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) || !IsBilibiliHost(uri.Host)) return;
        try
        {
            using var stream = await args.Response.GetContentAsync();
            using var reader = new StreamReader(stream.AsStreamForRead());
            var json = await reader.ReadToEndAsync();
            if (json.Length > 10_000_000) return;
            _lastPlayUrlUri = requestUri;
            var context = _pageContext is not null
                ? new PlayUrlContext(_pageContext.Url, _pageContext.Title, _pageContext.Bvid, _pageContext.Aid, _pageContext.Cid, _pageContext.EpisodeId, _pageContext.EpisodeTitle, _pageContext.Kind.Equals("bangumi", StringComparison.OrdinalIgnoreCase), ResolverStrategy.PlayUrlResponse)
                : ContextFromPlayUrl(uri);
            var result = _normalizer.Normalize(json, context);
            DispatcherQueue.TryEnqueue(() => ApplyResolveResult(result));
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("PlayURL response could not be read.", exception);
            DispatcherQueue.TryEnqueue(() => StatusText.Text = $"读取播放信息失败：{exception.Message}");
        }
    }

    private void ApplyResolveResult(ResolveResult result)
    {
        if (!result.IsSuccess)
        {
            _currentMedia = null;
            AddDownloadButton.IsEnabled = false;
            StatusText.Text = result.Error?.UserMessage ?? "无法解析当前媒体。";
            return;
        }

        _currentMedia = result.Media;
        _videoTracks = result.Media!.Tracks.Where(track => track.Type == TrackType.Video).ToList();
        QualityCombo.Items.Clear();
        foreach (var track in _videoTracks)
        {
            var quality = track.QualityId?.ToString(CultureInfo.InvariantCulture) ?? "Auto";
            QualityCombo.Items.Add($"{quality} · {track.Codec ?? "未知编码"} · {FormatBytes(track.Size)}");
        }
        if (QualityCombo.Items.Count > 0) QualityCombo.SelectedIndex = 0;
        else if (result.Media.LegacySegments.Count > 0) QualityCombo.PlaceholderText = $"DURL · {result.Media.LegacySegments.Count} 个分段";

        TitleText.Text = result.Media.Title;
        IdentityText.Text = $"{result.Media.Bvid ?? "未识别 BV"} · CID {result.Media.Cid?.ToString(CultureInfo.InvariantCulture) ?? "未知"} · 来源 {result.Media.Source}";
        TrackText.Text = result.Media.LegacySegments.Count > 0 && result.Media.Tracks.Count == 0
            ? $"DURL · {result.Media.LegacySegments.Count} 个分段"
            : $"视频 {result.Media.VideoTrack?.Codec ?? "—"} · 音频 {result.Media.AudioTrack?.Codec ?? "—"} · {result.Media.Tracks.Count} 条轨道";
        StatusText.Text = result.Media.Tracks.Count > 0 || result.Media.LegacySegments.Count > 0 ? "已捕获可用媒体轨道。" : "已捕获信息，但没有可用轨道。";
        AddDownloadButton.IsEnabled = _videoTracks.Count > 0 || result.Media.LegacySegments.Count > 0;
    }

    private async void AddDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMedia is null) return;
        try
        {
            MediaTrack? video = null;
            MediaTrack? audio = null;

            if (_videoTracks.Count > 0)
            {
                video = _videoTracks[Math.Clamp(QualityCombo.SelectedIndex, 0, _videoTracks.Count - 1)];
                audio = _currentMedia.Tracks.FirstOrDefault(track => track.Type == TrackType.Audio);
                if (audio is not null && AppServices.Settings.MergeAfterDownload && !AppServices.Ffmpeg.IsAvailable)
                {
                    StatusText.Text = "当前视频需要合并音视频，但未找到 FFmpeg。请先在“设置”中指定 ffmpeg.exe。";
                    return;
                }
            }
            else if (_currentMedia.LegacySegments.Count == 0)
            {
                return;
            }

            var title = AppServices.FileNames.Sanitize(_currentMedia.Title, "Bilibili video");
            var outputFile = AppServices.FileNames.GetAvailablePath(AppServices.Settings.DownloadDirectory, $"{title}.mp4");
            var requestHeaders = await CaptureRequestHeadersAsync();
            var request = new DownloadRequest(
                Guid.NewGuid(),
                _currentMedia,
                video,
                audio,
                AppServices.Settings.DownloadDirectory,
                Path.GetFileName(outputFile),
                new RetryPolicy(AppServices.Settings.MaxRetryAttempts),
                AppServices.Settings.MergeAfterDownload,
                AppServices.Settings.DeleteTemporaryFilesAfterMerge,
                requestHeaders);
            await AppServices.Downloads.EnqueueAsync(request);
            StatusText.Text = "已加入下载队列，可在“下载”页面查看进度。";
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Failed to create download task.", exception);
            StatusText.Text = $"创建下载任务失败：{exception.Message}";
        }
    }

    private async Task<MediaRequestHeaders> CaptureRequestHeadersAsync()
    {
        var pageUrl = _currentMedia?.PageUrl ?? AddressBox.Text;
        var core = BrowserWebView.CoreWebView2;
        if (core is null)
        {
            return new MediaRequestHeaders(pageUrl, "https://www.bilibili.com", null, null, _lastPlayUrlUri);
        }

        string? userAgent = null;
        string? cookieHeader = null;
        try
        {
            var scriptResult = await core.ExecuteScriptAsync("navigator.userAgent");
            userAgent = JsonSerializer.Deserialize<string>(scriptResult);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Could not read WebView2 user agent.", exception);
        }

        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(pageUrl);
            cookieHeader = string.Join("; ", cookies
                .GroupBy(cookie => cookie.Name, StringComparer.Ordinal)
                .Select(group => group.Last())
                .Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Could not copy Bilibili cookies into the in-memory download request.", exception);
        }

        return new MediaRequestHeaders(pageUrl, "https://www.bilibili.com", userAgent, cookieHeader, _lastPlayUrlUri);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(AddressBox.Text.Trim(), UriKind.Absolute, out var uri)) BrowserWebView.CoreWebView2?.Navigate(uri.ToString());
        else StatusText.Text = "请输入完整网址，例如 https://www.bilibili.com/video/BV…";
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserWebView.CoreWebView2?.CanGoBack == true) BrowserWebView.CoreWebView2.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserWebView.CoreWebView2?.CanGoForward == true) BrowserWebView.CoreWebView2.GoForward();
    }

    private static PlayUrlContext ContextFromPlayUrl(Uri uri)
    {
        var values = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]), StringComparer.OrdinalIgnoreCase);
        values.TryGetValue("bvid", out var bvid);
        long aid = 0;
        long cid = 0;
        long episodeId = 0;
        if (values.TryGetValue("avid", out var aidText)) _ = long.TryParse(aidText, out aid);
        if (values.TryGetValue("cid", out var cidText)) _ = long.TryParse(cidText, out cid);
        if (values.TryGetValue("ep_id", out var episodeText)) _ = long.TryParse(episodeText, out episodeId);
        return new PlayUrlContext(uri.ToString(), "Bilibili media", bvid, aid == 0 ? null : aid, cid == 0 ? null : cid, episodeId == 0 ? null : episodeId);
    }

    private static bool IsBilibiliHost(string host) => host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);
    private static string FormatBytes(long? bytes) => bytes is null or <= 0 ? "大小未知" : bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):F1} GB" : $"{bytes / (1024d * 1024):F0} MB";
}
