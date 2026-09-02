using System.Net;
using NovaClip.Infrastructure;

namespace NovaClip.App;

public static class AppServices
{
    public const string CurrentVersion = "1.0.0-beta.6";

    public static WindowsSettingsStore Settings { get; } = new();
    public static SqliteDownloadTaskRepository Repository { get; private set; } = null!;
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
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);

    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized)
        {
            return;
        }

        await InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (IsInitialized)
            {
                return;
            }

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
            Repository = new SqliteDownloadTaskRepository(ResolveDatabasePath());
            Downloads = new DownloadManager(Downloader, Ffmpeg, Repository, Repository, Settings.MaxConcurrentTasks);
            UpdateService = new GitHubReleaseUpdateService(UpdateHttpClient, Settings.UpdateFeedRepository, WindowsSettingsStore.GitHubToken);
            UpdateCoordinator = new WindowsUpdateCoordinator(UpdateService, Settings);

            StartupDiagnostics.Info("Initializing SQLite repository.");
            await Repository.InitializeAsync(cancellationToken).ConfigureAwait(true);

            StartupDiagnostics.Info("Restoring download tasks.");
            await Downloads.RestoreAsync(cancellationToken).ConfigureAwait(true);

            IsInitialized = true;
            StartupDiagnostics.Info("Application services initialized.");
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    private static string ResolveDatabasePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "novaclip.db");
    }
}
