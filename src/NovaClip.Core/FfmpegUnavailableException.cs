namespace NovaClip.Core;

public sealed class FfmpegUnavailableException : InvalidOperationException
{
    public FfmpegUnavailableException()
        : base("FFmpeg is required to merge DASH tracks or remux DURL media. Configure FFmpeg before adding this download.") { }

    public FfmpegUnavailableException(string message) : base(message) { }

    public FfmpegUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
