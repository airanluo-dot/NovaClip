using System.Net;
using BiliNative.Infrastructure;

namespace BiliNative.App;

public static class AppServices
{
    public const string CurrentVersion = "1.0.0-beta.2";

    public static WindowsSettingsStore Settings { get; } = new();
    public static SqliteDownloadTaskRepository Repository { get; } = new(ResolveDatabasePath());
    public static FileNameSanitizer FileNames { get; } = new();

    public static HttpClient MediaHttpClient { get; private set; } = null!;
    public static HttpClient UpdateHttpClient { get; private set; } = null!;
    public static HttpRangeDownloader Downloader { get; private set; } = null!;
    public static WindowsFfmpegService Ffmpeg { get; private set; } = null!;
    public static DownloadManager Downloads { get; private set; } = null!;
    public static GitHubReleaseUpdateService UpdateService { get; private set; } = null!;
    public static WindowsUpdateCoordinator UpdateCoordinator { get; private set; } = null!;
    public static bool IsPortableInstall { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker"));
    public static bool IsInitialized { get; private set; }

    public static async Task InitializeAsync()
    {
        if (IsInitialized) return;

        StartupDiagnostics.Info("Loading settings.");
        await Settings.LoadAsync().ConfigureAwait(true);

        MediaHttpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            AllowAutoRedirect = true
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        UpdateHttpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        Downloader = new HttpRangeDownloader(MediaHttpClient);
        Ffmpeg = new WindowsFfmpegService(Settings);
        Downloads = new DownloadManager(Downloader, Ffmpeg, Repository, Repository, Settings.MaxConcurrentTasks);
        UpdateService = new GitHubReleaseUpdateService(UpdateHttpClient, Settings.UpdateFeedRepository, WindowsSettingsStore.GitHubToken);
        UpdateCoordinator = new WindowsUpdateCoordinator(UpdateService, Settings);

        StartupDiagnostics.Info("Initializing SQLite repository.");
        await Repository.InitializeAsync().ConfigureAwait(true);

        StartupDiagnostics.Info("Restoring download tasks.");
        await Downloads.RestoreAsync().ConfigureAwait(true);

        IsInitialized = true;
        StartupDiagnostics.Info("Application services initialized.");
    }

    private static string ResolveDatabasePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "novaclip.db");
    }
}
