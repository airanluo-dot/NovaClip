using System.Text.Json;
using System.Globalization;
using BiliNative.Core;

namespace BiliNative.WebBridge;

public sealed class PlayUrlNormalizer : IPlayUrlNormalizer
{
    public ResolveResult Normalize(string json, PlayUrlContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = GetInt64(root, "code") ?? 0;
            if (code != 0)
            {
                return ResolveResult.Failure(ToError(code, GetString(root, "message")));
            }

            var data = SelectData(root);
            if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return ResolveResult.Failure(new AppError("RESOLVE_PLAYURL_NOT_FOUND", "没有找到可用的播放信息。", "PlayURL response did not contain data.", true, "刷新页面后重试"));
            }
            if (data.TryGetProperty("video_info", out var videoInfo)) data = videoInfo;

            var qualities = ParseQualityOptions(data);
            var codecs = ParseCodecOptions(data);
            var tracks = new List<MediaTrack>();
            var legacySegments = new List<LegacyMediaSegment>();
            if (data.TryGetProperty("dash", out var dash) && dash.ValueKind == JsonValueKind.Object)
            {
                ParseTracks(dash, "video", TrackType.Video, tracks);
                ParseTracks(dash, "audio", TrackType.Audio, tracks);
            }
            if (data.TryGetProperty("durl", out var durl) && durl.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var segment in durl.EnumerateArray())
                {
                    var urls = ParseUrls(segment, "url");
                    if (urls.Count > 0) legacySegments.Add(new LegacyMediaSegment(index++, urls, GetInt64(segment, "size"), GetDouble(segment, "length") is double length ? length / 1000 : null));
                }
            }

            if (tracks.Count == 0 && legacySegments.Count == 0)
            {
                return ResolveResult.Failure(new AppError("RESOLVE_PLAYURL_NOT_FOUND", "当前页面没有可下载的媒体轨道。", "Neither DASH nor DURL media data was found.", true, "确认视频可以正常播放"));
            }

            var media = new MediaDescriptor
            {
                Title = context.Title,
                PageUrl = context.PageUrl,
                Bvid = context.Bvid ?? GetString(data, "bvid"),
                Aid = context.Aid ?? GetInt64(data, "avid") ?? GetInt64(data, "aid"),
                Cid = context.Cid ?? GetInt64(data, "cid"),
                EpisodeId = context.EpisodeId ?? GetInt64(data, "ep_id") ?? GetInt64(data, "episode_id"),
                EpisodeTitle = context.EpisodeTitle,
                IsBangumi = context.IsBangumi,
                Source = context.Source,
                QualityOptions = qualities,
                CodecOptions = codecs,
                Tracks = tracks,
                LegacySegments = legacySegments
            };
            return ResolveResult.Success(media);
        }
        catch (JsonException exception)
        {
            return ResolveResult.Failure(new AppError("RESOLVE_PARSE_CHANGED", "B 站返回的数据格式暂时无法识别。", exception.Message, true, "更新页面后重试", exception));
        }
    }

    private static JsonElement SelectData(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data)) return data;
        if (root.TryGetProperty("result", out var result)) return result;
        return default;
    }

    private static void ParseTracks(JsonElement dash, string property, TrackType type, List<MediaTrack> tracks)
    {
        if (!dash.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var urls = ParseUrls(item, "base_url", "baseUrl");
            var backupUrls = ParseUrls(item, "backup_url", "backupUrl");
            urls = urls.Concat(backupUrls).GroupBy(candidate => candidate.Url, StringComparer.Ordinal).Select(group => group.First()).ToList();
            if (urls.Count == 0) continue;
            var quality = GetInt32(item, "id") ?? GetInt32(item, "quality");
            var codecId = GetInt32(item, "codecs") ?? GetInt32(item, "codecid");
            tracks.Add(new MediaTrack
            {
                Type = type,
                TrackId = $"{type.ToString().ToLowerInvariant()}-{quality?.ToString(CultureInfo.InvariantCulture) ?? index.ToString(CultureInfo.InvariantCulture)}-{codecId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
                QualityId = quality,
                CodecId = codecId,
                Codec = GetString(item, "codecs") ?? GetString(item, "codec") ?? CodecName(codecId),
                Size = GetInt64(item, "size"),
                DurationSeconds = GetDouble(item, "duration") is double duration ? duration / 1000 : null,
                Urls = urls
            });
            index++;
        }
    }

    private static List<MediaUrlCandidate> ParseUrls(JsonElement element, params string[] primaryNames)
    {
        var urls = new List<MediaUrlCandidate>();
        foreach (var name in primaryNames)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) urls.Add(new MediaUrlCandidate(value.GetString()!));
            if (element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array)
            {
                urls.AddRange(value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => new MediaUrlCandidate(item.GetString()!)).Where(item => !string.IsNullOrWhiteSpace(item.Url)));
            }
        }
        return urls;
    }

    private static QualityOption[] ParseQualityOptions(JsonElement data)
    {
        var ids = data.TryGetProperty("accept_quality", out var quality) && quality.ValueKind == JsonValueKind.Array ? quality.EnumerateArray().Where(item => item.TryGetInt32(out _)).Select(item => item.GetInt32()).ToArray() : [];
        var descriptions = data.TryGetProperty("accept_description", out var description) && description.ValueKind == JsonValueKind.Array ? description.EnumerateArray().Select(item => item.GetString() ?? "未知清晰度").ToArray() : [];
        if (data.TryGetProperty("support_formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in formats.EnumerateArray())
            {
                var id = GetInt32(item, "quality");
                if (id is null || ids.Contains(id.Value)) continue;
                ids = [.. ids, id.Value];
                descriptions = [.. descriptions, GetString(item, "new_description") ?? GetString(item, "display_desc") ?? "未知清晰度"];
            }
        }
        return ids.Select((id, index) => new QualityOption(id, index < descriptions.Length ? descriptions[index] : QualityLabel(id))).ToArray();
    }

    private static CodecOption[] ParseCodecOptions(JsonElement data)
    {
        if (!data.TryGetProperty("support_formats", out var formats) || formats.ValueKind != JsonValueKind.Array) return [];
        return formats.EnumerateArray().Select(item => new CodecOption(GetInt32(item, "new_id") ?? GetInt32(item, "quality") ?? 0, GetString(item, "codecs") ?? GetString(item, "format") ?? "Auto")).Where(item => item.Id != 0).DistinctBy(item => item.Id).ToArray();
    }

    private static AppError ToError(long code, string? message) => code switch
    {
        -10403 => new AppError("RESOLVE_VIP_REQUIRED", "此视频需要拥有相应权限的账号才能播放。", message ?? "VIP or permission required.", false, "请在 B 站确认账号有权播放该内容"),
        -101 => new AppError("RESOLVE_LOGIN_REQUIRED", "请先在 B 站登录。", message ?? "Login required.", true, "在浏览器页登录 B 站"),
        _ => new AppError("RESOLVE_PLAYURL_ERROR", message ?? "B 站暂时无法提供播放信息。", $"Bilibili returned code {code}.", code >= 500, "刷新页面后重试")
    };

    private static string QualityLabel(int id) => id switch
    {
        16 => "360P 流畅",
        32 => "480P 标清",
        64 => "720P 准高清",
        80 => "1080P 高清",
        112 => "1080P 高码率",
        120 => "4K 超高清",
        125 => "HDR 真彩",
        _ => $"清晰度 {id}"
    };

    private static string? CodecName(int? id) => id switch { 7 => "AVC", 12 => "HEVC", 13 => "AV1", _ => null };
    private static string? GetString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? GetInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) ? text : null;
    }

    private static int? GetInt32(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) ? text : null;
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) ? text : null;
    }
}
