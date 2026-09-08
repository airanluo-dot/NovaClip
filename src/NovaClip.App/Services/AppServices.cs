using System.Net;
using System.Reflection;
using NovaClip.Infrastructure;

namespace NovaClip.App;

public static class AppServices
{
    public static string CurrentVersion =>
        typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("The application informational version is missing.");

    public static WindowsSettingsStore Settings { get; } = new();
    public static SqliteDownloadTaskRepository Repository { get; private set; } = null!;
    public static FileNameSanitizer FileNames { get; } = new();
    public static OutputReservationService Reservations { get; private set; } = null!;

    public static HttpClient MediaHttpClient { get; private set; } = null!;
    public static HttpClient UpdateHttpClient { get; private set; } = null!;
    public static HttpRangeDownloader Downloader { get; private set; } = null!;
    public static WindowsFfmpegService Ffmpeg { get; private set; } = null!;
    public static DownloadManager Downloads { get; private set; } = null!;
    public static GitHubReleaseUpdateService UpdateService { get; private set; } = null!;
    public static WindowsUpdateCoordinator UpdateCoordinator { get; private set; } = null!;
    public static SettingsApplicationCoordinator SettingsCoordinator { get; private set; } = null!;
    public static bool IsPortableInstall { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker"));
    public static bool IsInitialized { get; private set; }
    public static Task ShutdownTask => _shutdownTask ?? Task.CompletedTask;

    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static readonly SemaphoreSlim ShutdownGate = new(1, 1);
    private static Task? _backgroundUpdateCheckTask;
    private static Task? _shutdownTask;
    private static int _shutdownRequested;
    private static int _shutdownCompleted;

    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized) return;

        await InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (IsInitialized) return;
            Interlocked.Exchange(ref _shutdownRequested, 0);
            Interlocked.Exchange(ref _shutdownCompleted, 0);

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
            Reservations = new OutputReservationService();
            Downloads = new DownloadManager(
                Downloader,
                Ffmpeg,
                Repository,
                Repository,
                Settings.MaxConcurrentTasks,
                Reservations,
                Repository);
            UpdateService = new GitHubReleaseUpdateService(UpdateHttpClient, Settings.UpdateFeedRepository, WindowsSettingsStore.GitHubToken);
            UpdateCoordinator = new WindowsUpdateCoordinator(UpdateService, Settings);
            SettingsCoordinator = new SettingsApplicationCoordinator(Settings, Downloads);

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

    public static void StartBackgroundUpdateCheck()
    {
        if (!IsInitialized || Volatile.Read(ref _shutdownRequested) != 0 || _backgroundUpdateCheckTask is not null) return;
        _backgroundUpdateCheckTask = UpdateCoordinator.CheckSilentlyAsync();
    }

    public static async Task PrepareForUpdateAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        if (!IsInitialized || Downloads is null) return;
        await Downloads.ShutdownAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    public static void BeginShutdown()
    {
        _shutdownTask ??= ShutdownAsyncCore();
    }

    private static async Task ShutdownAsyncCore()
    {
        if (!IsInitialized || Interlocked.Exchange(ref _shutdownCompleted, 1) != 0) return;

        await ShutdownGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _shutdownRequested, 1);
            try { UpdateCoordinator?.Stop(); } catch (Exception exception) { StartupDiagnostics.Warning("Update check cancellation failed.", exception); }
            if (Downloads is not null)
            {
                try { await Downloads.ShutdownAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
                catch (Exception exception) { StartupDiagnostics.Warning("Download shutdown did not complete within the grace period.", exception); }
                try { await Downloads.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { StartupDiagnostics.Warning("Download service disposal failed.", exception); }
            }

            if (_backgroundUpdateCheckTask is not null)
            {
                try { await _backgroundUpdateCheckTask.ConfigureAwait(false); }
                catch (Exception exception) { StartupDiagnostics.Warning("Background update check did not complete cleanly.", exception); }
            }

            try { UpdateCoordinator?.Dispose(); } catch (Exception exception) { StartupDiagnostics.Warning("Update coordinator disposal failed.", exception); }
            try { UpdateService?.Dispose(); } catch (Exception exception) { StartupDiagnostics.Warning("Update service disposal failed.", exception); }
            try { MediaHttpClient?.Dispose(); } catch (Exception exception) { StartupDiagnostics.Warning("Media HTTP client disposal failed.", exception); }
            try { UpdateHttpClient?.Dispose(); } catch (Exception exception) { StartupDiagnostics.Warning("Update HTTP client disposal failed.", exception); }
            try { Reservations?.Dispose(); } catch (Exception exception) { StartupDiagnostics.Warning("Output reservation disposal failed.", exception); }
            try { Repository?.Dispose(); } catch (Exception exception) { StartupDiagnostics.Warning("Repository disposal failed.", exception); }

            StartupDiagnostics.Info("Application services shut down.");
        }
        finally
        {
            ShutdownGate.Release();
        }
    }

    private static string ResolveDatabasePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "novaclip.db");
    }
}
