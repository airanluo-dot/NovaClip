using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Bilibili;

public static class BilibiliJsonParser
{
    private const int MaxPageDataLength = 10_000_000;

    public static bool TryParsePageContext(string json, string pageUrl, out PlayUrlContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPageDataLength || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) || pageUri.Scheme is not ("http" or "https") || !IsBilibiliHost(pageUri.Host)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return false;
            var video = FindObject(root, "videoData") ?? FindObject(root, "videoInfo") ?? root;
            var title = GetString(video, "title") ?? GetString(video, "long_title") ?? "Bilibili media";
            var bvid = GetString(video, "bvid");
            var aid = GetInt64(video, "aid") ?? GetInt64(video, "avid");
            var cid = GetInt64(video, "cid");
            if (string.IsNullOrWhiteSpace(bvid) && aid is null && cid is null) return false;
            context = new PlayUrlContext(pageUri.ToString(), title, bvid, aid, cid, Source: ResolverStrategy.PageData);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static JsonElement? FindObject(JsonElement element, string property, int depth = 0)
    {
        if (depth > 64) return null;
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object) return value;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in element.EnumerateObject())
            {
                var result = FindObject(child.Value, property, depth + 1);
                if (result is not null) return result;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var result = FindObject(child, property, depth + 1);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement? element, string property) => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var propertyValue) && propertyValue.ValueKind == JsonValueKind.String ? propertyValue.GetString() : null;
    private static long? GetInt64(JsonElement? element, string property)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(property, out var propertyValue)) return null;
        if (propertyValue.ValueKind == JsonValueKind.Number && propertyValue.TryGetInt64(out var number)) return number;
        return propertyValue.ValueKind == JsonValueKind.String && long.TryParse(propertyValue.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var text) ? text : null;
    }

    private static bool IsBilibiliHost(string host)
    {
        var normalized = host.TrimEnd('.');
        return normalized.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("b23.tv", StringComparison.OrdinalIgnoreCase);
    }
}
