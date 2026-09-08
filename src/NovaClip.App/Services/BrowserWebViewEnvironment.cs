using System.Globalization;
using Microsoft.Web.WebView2.Core;

namespace NovaClip.App;

internal static class BrowserWebViewEnvironment
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _sharedEnvironmentTask;

    public static async Task VerifyAsync()
    {
        _ = await GetAsync().ConfigureAwait(false);
        StartupDiagnostics.Info("WebView2.EnvironmentReady");
        StartupDiagnostics.Info("WebView2.Ready");
    }

    public static Task<CoreWebView2Environment> GetAsync()
    {
        lock (Gate)
        {
            return _sharedEnvironmentTask ??= CreateAsync();
        }
    }

    public static void ResetIfFailed(Task<CoreWebView2Environment> failedTask)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_sharedEnvironmentTask, failedTask))
            {
                _sharedEnvironmentTask = null;
            }
        }
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaClip",
            "WebView2");
        try
        {
            Directory.CreateDirectory(profilePath);
            return await CoreWebView2Environment.CreateWithOptionsAsync(null, profilePath, null);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("The primary WebView2 profile could not be created; trying a temporary profile.", exception);
            var fallbackPath = Path.Combine(
                Path.GetTempPath(),
                "NovaClip",
                "WebView2",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(fallbackPath);
            return await CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackPath, null);
        }
    }
}
