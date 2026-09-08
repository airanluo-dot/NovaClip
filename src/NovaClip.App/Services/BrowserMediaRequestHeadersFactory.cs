using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using NovaClip.Core;

namespace NovaClip.App;

internal static class BrowserMediaRequestHeadersFactory
{
    private const int MaxCookieHeaderCharacters = 64_000;

    public static async Task<MediaRequestHeaders?> CreateAsync(CoreWebView2? core, string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
        {
            return null;
        }

        if (core is null)
        {
            return new MediaRequestHeaders(
                Referer: pageUri.ToString(),
                Origin: pageUri.GetLeftPart(UriPartial.Authority),
                RefreshUrl: pageUri.ToString());
        }

        string? cookieHeader = null;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(pageUri.ToString());
            var builder = new StringBuilder();
            foreach (var cookie in cookies)
            {
                if (string.IsNullOrWhiteSpace(cookie.Name) ||
                    cookie.Name.IndexOfAny(['\r', '\n', ';', '=']) >= 0 ||
                    cookie.Value.IndexOfAny(['\r', '\n', ';']) >= 0)
                {
                    continue;
                }

                var separatorLength = builder.Length == 0 ? 0 : 2;
                if (builder.Length + separatorLength + cookie.Name.Length + 1 + cookie.Value.Length > MaxCookieHeaderCharacters)
                {
                    break;
                }

                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }

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

        return new MediaRequestHeaders(
            pageUri.ToString(),
            pageUri.GetLeftPart(UriPartial.Authority),
            userAgent,
            cookieHeader,
            pageUri.ToString());
    }
}
