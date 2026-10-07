namespace NovaClip.Infrastructure;

public static class FfmpegCommandArguments
{
    public static IReadOnlyList<string> ForMp4Mux(string videoPath, string audioPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        // The transaction writes to final-output.tmp; FFmpeg cannot infer its muxer
        // from that staging extension. Keep arguments separate for ProcessStartInfo.
        return ["-hide_banner", "-nostdin", "-y", "-i", videoPath, "-i", audioPath,
            "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-f", "mp4", outputPath];
    }

    public static IReadOnlyList<string> ForMp4Concat(string manifestPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        return ["-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "1", "-i", manifestPath,
            "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-f", "mp4", outputPath];
    }
}
