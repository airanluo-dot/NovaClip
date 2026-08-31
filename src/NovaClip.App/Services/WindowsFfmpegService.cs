using System.Diagnostics;
using NovaClip.Core;

namespace NovaClip.App;

public sealed class WindowsFfmpegService : IFfmpegService
{
    private readonly WindowsSettingsStore _settings;

    public WindowsFfmpegService(WindowsSettingsStore settings)
    {
        _settings = settings;
    }

    public bool IsAvailable => FindFfmpeg() is not null;

    public async Task<FfmpegResult> MergeAsync(string videoPath, string audioPath, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var executable = FindFfmpeg();
        if (executable is null) return new FfmpegResult(false, -1, null, "找不到 ffmpeg.exe。请在设置中选择 FFmpeg，或将其放入应用目录 tools/ffmpeg/win-x64/。");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(audioPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("1:a:0");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return new FfmpegResult(false, -1, null, "无法启动 ffmpeg.exe。");
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            progress?.Report(1);
            var success = process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            return new FfmpegResult(success, process.ExitCode, success ? outputPath : null, success ? null : error.Trim());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception)
        {
            TryKill(process);
            return new FfmpegResult(false, -1, null, exception.Message);
        }
    }

    private string? FindFfmpeg()
    {
        var candidates = new[]
        {
            _settings.FfmpegPath,
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "win-x64", "ffmpeg.exe"),
            "ffmpeg.exe"
        };
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && (Path.IsPathRooted(candidate) ? File.Exists(candidate) : IsOnPath(candidate!)));
    }

    private static bool IsOnPath(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = "where.exe", Arguments = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            process?.WaitForExit(2000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch
        {
            // The process may have exited between the check and Kill.
        }
    }
}
