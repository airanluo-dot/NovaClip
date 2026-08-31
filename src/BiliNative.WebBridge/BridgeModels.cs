using System.Text.Json;

namespace BiliNative.WebBridge;

public enum BilibiliBridgeMessageType
{
    Unknown,
    BridgeReady,
    PageContextChanged,
    PlayerQualityChanged,
    HydrateDataFound,
    BridgeError
}

public sealed record BilibiliPageContext(
    string Url,
    string Kind,
    long? Aid,
    string? Bvid,
    long? Cid,
    long? EpisodeId,
    int? Page,
    string Title,
    string? EpisodeTitle = null);

public sealed record BilibiliBridgeMessage(
    int SchemaVersion,
    BilibiliBridgeMessageType Type,
    JsonElement Payload);

public static class BilibiliBridgeMessageParser
{
    public static bool TryParse(string json, out BilibiliBridgeMessage? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 || !root.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String || !root.TryGetProperty("payload", out var payload)) return false;
            var type = typeProperty.GetString() switch
            {
                "bridgeReady" => BilibiliBridgeMessageType.BridgeReady,
                "pageContextChanged" => BilibiliBridgeMessageType.PageContextChanged,
                "playerQualityChanged" => BilibiliBridgeMessageType.PlayerQualityChanged,
                "hydrateDataFound" => BilibiliBridgeMessageType.HydrateDataFound,
                "bridgeError" => BilibiliBridgeMessageType.BridgeError,
                _ => BilibiliBridgeMessageType.Unknown
            };
            if (type == BilibiliBridgeMessageType.Unknown || payload.GetRawText().Length > 2_000_000) return false;
            message = new BilibiliBridgeMessage(1, type, payload.Clone());
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
    }

    public static bool TryReadPageContext(BilibiliBridgeMessage message, out BilibiliPageContext? context)
    {
        context = null;
        if (message.Type != BilibiliBridgeMessageType.PageContextChanged) return false;
        var payload = message.Payload;
        if (!payload.TryGetProperty("url", out var urlProperty) || !Uri.TryCreate(urlProperty.GetString(), UriKind.Absolute, out var uri) || !IsBilibiliHost(uri.Host)) return false;
        var kind = payload.TryGetProperty("kind", out var kindProperty) ? kindProperty.GetString() ?? "unknown" : "unknown";
        context = new BilibiliPageContext(
            uri.ToString(),
            kind,
            GetInt64(payload, "aid"),
            GetString(payload, "bvid"),
            GetInt64(payload, "cid"),
            GetInt64(payload, "episodeId"),
            GetInt32(payload, "page"),
            GetString(payload, "title") ?? "Bilibili media",
            GetString(payload, "episodeTitle"));
        return true;
    }

    private static bool IsBilibiliHost(string host) => host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? GetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var text) ? text : null;
    }

    private static int? GetInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var text) ? text : null;
    }
}
