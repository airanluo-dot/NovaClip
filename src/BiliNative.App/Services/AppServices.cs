using System.Net;
using BiliNative.Core;
using BiliNative.Infrastructure;

namespace BiliNative.App;

public static class AppServices
{
    public const string CurrentVersion = "1.0.0-beta.1";
    public static WindowsSettingsStore Settings { get; } = new();
    public static HttpClient HttpClient { get; } = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false
    });
    public static SqliteDownloadTaskRepository Repository { get; } = new(ResolveDatabasePath());
    public static HttpRangeDownloader Downloader { get; } = new(HttpClient);
    public static GitHubReleaseUpdateService UpdateService { get; } = new(HttpClient);
    public static WindowsUpdateCoordinator UpdateCoordinator { get; } = new(UpdateService, Settings);
    public static DownloadManager Downloads { get; } = new(Downloader, new WindowsFfmpegService(Settings), Repository, Repository, Settings.MaxConcurrentTasks);
    public static FileNameSanitizer FileNames { get; } = new();
    public static bool IsPortableInstall { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker"));

    public static async Task InitializeAsync()
    {
        await Repository.InitializeAsync().ConfigureAwait(true);
        await Settings.LoadAsync().ConfigureAwait(true);
        await Downloads.RestoreAsync().ConfigureAwait(true);
    }

    private static string ResolveDatabasePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "novaclip.db");
    }
}
