using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Bilibili;

public static class BilibiliJsonParser
{
    public static bool TryParsePageContext(string json, string pageUrl, out PlayUrlContext? context)
    {
        context = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var video = FindObject(root, "videoData") ?? FindObject(root, "videoInfo") ?? root;
            var title = GetString(video, "title") ?? GetString(video, "long_title") ?? "Bilibili media";
            var bvid = GetString(video, "bvid");
            var aid = GetInt64(video, "aid") ?? GetInt64(video, "avid");
            var cid = GetInt64(video, "cid");
            if (string.IsNullOrWhiteSpace(bvid) && aid is null && cid is null) return false;
            context = new PlayUrlContext(pageUrl, title, bvid, aid, cid, Source: ResolverStrategy.PageData);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement? FindObject(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object) return value;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in element.EnumerateObject())
            {
                var result = FindObject(child.Value, property);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement? element, string property) => element is { } value && value.TryGetProperty(property, out var propertyValue) && propertyValue.ValueKind == JsonValueKind.String ? propertyValue.GetString() : null;
    private static long? GetInt64(JsonElement? element, string property)
    {
        if (element is not { } value || !value.TryGetProperty(property, out var propertyValue)) return null;
        if (propertyValue.ValueKind == JsonValueKind.Number && propertyValue.TryGetInt64(out var number)) return number;
        return propertyValue.ValueKind == JsonValueKind.String && long.TryParse(propertyValue.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var text) ? text : null;
    }
}
