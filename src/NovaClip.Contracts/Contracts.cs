namespace NovaClip.Contracts;

public enum BrowserNavigationKind { User, AddressBar, NewWindow, Redirect, Script }
public enum BrowserNavigationDecision { NavigateInCurrentView, OpenInSystemBrowser, Block, AskUser }
public enum MediaDetectionState { Idle, WaitingForPageContext, Observing, CandidateFound, Resolving, Ready, Unsupported, PermissionDenied, Expired, Error }
public enum UpdateChannel { Stable, Preview }

public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, DateTimeOffset? Expires, bool IsHttpOnly, bool IsSecure);
public sealed record PageIdentity(string PageUrl, string? Bvid, long? Aid, long? Cid, long? EpisodeId, long NavigationGeneration);
public sealed record MediaFingerprint(string PageUrl, string? Bvid, long? Aid, long? Cid, long? EpisodeId, int? QualityId, string? Codec, long NavigationGeneration);
public sealed record DetectionDiagnostic(string EventCode, MediaDetectionState State, DateTimeOffset Timestamp, string? Detail = null);

public interface IBrowserNavigationPolicy { BrowserNavigationDecision Evaluate(Uri uri, BrowserNavigationKind kind); }
public interface IBrowserNavigationService { Uri? CurrentUri { get; } bool CanGoBack { get; } bool CanGoForward { get; } void Navigate(Uri uri); void GoBack(); void GoForward(); void Reload(); void StopLoading(); }
public interface IBrowserHistoryService { IReadOnlyList<Uri> Entries { get; } }
public interface IBrowserTab { string Id { get; } Uri? Uri { get; } string? Title { get; } }
public interface IBrowserTabService { IBrowserTab Current { get; } IReadOnlyList<IBrowserTab> Tabs { get; } }
public interface IBrowserSessionService { Task InitializeAsync(CancellationToken cancellationToken); Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(Uri scope, CancellationToken cancellationToken); Task ClearSessionAsync(CancellationToken cancellationToken); string UserDataFolder { get; } }
public interface IBrowserDiagnosticsService { Task<string> ExportAsync(CancellationToken cancellationToken); }
public interface IExternalUriLauncher { Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken); }
public interface IBrowserHomeService { Uri HomeUri { get; } }

public interface IPageContextSource { event EventHandler<PageIdentity>? Changed; PageIdentity? Current { get; } }
public interface IPlayUrlObservationSource { event EventHandler<PlayUrlObservation>? Observed; }
public sealed record PlayUrlObservation(Uri Endpoint, string Json, long NavigationGeneration, DateTimeOffset ObservedAt);
public interface IBilibiliPageContextProvider { Task<PageIdentity?> GetAsync(Uri pageUri, long generation, CancellationToken cancellationToken); }
public interface IBilibiliApiClient { Task<string> GetPlayUrlAsync(PageIdentity page, CancellationToken cancellationToken); }
public interface IBilibiliEndpointProvider { Uri GetPlayUrlEndpoint(PageIdentity page); }
public interface IBilibiliSessionAdapter { Task<IReadOnlyDictionary<string, string>> GetRequestCookiesAsync(CancellationToken cancellationToken); }
public interface IBilibiliUrlResolver { bool TryResolve(string input, out Uri uri); }
public interface IMediaDetectionStrategy { string Name { get; } Task<MediaDetectionResult> TryResolveAsync(PageIdentity page, CancellationToken cancellationToken); }
public interface IMediaDetectionCoordinator { event EventHandler<MediaDetectionSnapshot>? StateChanged; MediaDetectionSnapshot Snapshot { get; } long BeginNavigation(Uri uri); Task ObserveAsync(PlayUrlObservation observation, CancellationToken cancellationToken = default); Task DetectAsync(CancellationToken cancellationToken = default); void Reset(); }
public sealed record MediaDetectionResult(bool Success, MediaDetectionState State, MediaFingerprint? Fingerprint, object? Media, string? ErrorCode = null);
public sealed record MediaDetectionSnapshot(MediaDetectionState State, PageIdentity? Page, MediaFingerprint? Fingerprint, object? Media, string? ErrorCode, IReadOnlyList<DetectionDiagnostic> Diagnostics);

public interface IDownloadQueueService { Task<Guid> EnqueueAsync(object request, CancellationToken cancellationToken = default); }
public interface IDownloadTaskService { Task PauseAsync(Guid id, CancellationToken cancellationToken); Task ResumeAsync(Guid id, CancellationToken cancellationToken); Task RetryAsync(Guid id, CancellationToken cancellationToken); Task CancelAsync(Guid id, CancellationToken cancellationToken); }
public interface ITrackDownloader { Task DownloadAsync(Uri source, string destination, CancellationToken cancellationToken); }
public interface IResumeService { Task<long> GetResumeOffsetAsync(string path, CancellationToken cancellationToken); }
public interface IDownloadPersistence { Task SaveAsync(object snapshot, CancellationToken cancellationToken); }
public interface IDownloadHistoryRepository { Task<IReadOnlyList<object>> GetAsync(CancellationToken cancellationToken); }
public interface IDownloadProgressSource { event EventHandler<object>? ProgressChanged; }
public interface IDownloadFileNamingService { string CreateFileName(string title, string extension); }

public interface IMediaMerger { Task MergeAsync(string video, string audio, string output, CancellationToken cancellationToken); }
public interface IFfmpegLocator { Task<string?> LocateAsync(CancellationToken cancellationToken); }
public interface IFfmpegValidator { Task<bool> ValidateAsync(string executable, CancellationToken cancellationToken); }
public interface IMediaOutputValidator { Task<bool> ValidateAsync(string path, CancellationToken cancellationToken); }
public interface IAudioExtractor { Task ExtractAsync(string source, string destination, CancellationToken cancellationToken); }
public interface IMediaTranscoder { Task TranscodeAsync(string source, string destination, CancellationToken cancellationToken); }

public interface ISettingsService { T GetValue<T>(string key, T defaultValue); Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default); }
public interface ISettingsStore { Task<string?> ReadAsync(CancellationToken cancellationToken); Task WriteAtomicAsync(string json, CancellationToken cancellationToken); }
public interface ISettingsMigrationService { Task MigrateAsync(CancellationToken cancellationToken); }
public interface ISettingsDefaultsProvider { object GetDefaults(); }
public interface ILocalizationService { string GetString(string key); string Format(string key, params object[] args); }
public interface IAppLanguageService { string CurrentLanguage { get; } Task SetLanguageAsync(string language, CancellationToken cancellationToken); }

public interface IUpdateService { Task<object?> CheckAsync(CancellationToken cancellationToken); }
public interface IUpdateFeed { Task<string> GetManifestAsync(UpdateChannel channel, CancellationToken cancellationToken); }
public interface IUpdatePackageVerifier { Task<bool> VerifyAsync(string packagePath, string expectedHash, CancellationToken cancellationToken); }
public interface IUpdateInstaller { Task InstallAsync(string packagePath, CancellationToken cancellationToken); }
public interface IUpdateChannelProvider { UpdateChannel Current { get; } }

public interface IFilePickerService { Task<string?> PickFileAsync(IReadOnlyList<string> extensions, CancellationToken cancellationToken); Task<string?> PickFolderAsync(CancellationToken cancellationToken); }
public interface IFolderLauncher { Task<bool> OpenAsync(string path, CancellationToken cancellationToken); }
public interface INotificationService { Task ShowAsync(string titleKey, string messageKey, CancellationToken cancellationToken); }
public interface IClipboardService { Task SetTextAsync(string text, CancellationToken cancellationToken); }
public interface IAppLifecycleService { Task RestartAsync(CancellationToken cancellationToken); }
public interface IWindowService { void SetMinimumSize(int width, int height); }
public interface IThemeService { string Current { get; } void Apply(string theme); }
public interface ITrayService { }
public interface IAutoStartService { }
public interface IJumpListService { }

public interface IBatchMediaEnumerator { }
public interface IMultiPartMediaProvider { }
public interface ISubtitleProvider { }
public interface IDanmakuProvider { }
public interface ICoverArtProvider { }
public interface IMediaMetadataProvider { }
public interface IAudioTrackProvider { }
public interface IPlaylistProvider { }
public interface ISeasonProvider { }
public interface IStreamProbeService { }
public interface ISpeedLimitService { }
public interface ISchedulerService { }
