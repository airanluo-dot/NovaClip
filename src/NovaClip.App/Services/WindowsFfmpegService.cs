using System.Diagnostics;
using System.Text;
using NovaClip.Core;

namespace NovaClip.App;

public sealed class WindowsFfmpegService : IFfmpegService
{
    private const int MaxCapturedCharacters = 32_000;
    private const string StagingDirectoryName = ".novaclip";
    private readonly WindowsSettingsStore _settings;

    public WindowsFfmpegService(WindowsSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public bool IsAvailable => FindFfmpeg() is not null;
    public string? Locate() => FindFfmpeg();

    public async Task<FfmpegResult> MergeAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsStagingOutputPath(outputPath))
        {
            return new FfmpegResult(false, -1, null, "FFmpeg output must be a NovaClip staging file.");
        }

        var executable = FindFfmpeg();
        if (executable is null)
        {
            return new FfmpegResult(false, -1, null, "找不到 ffmpeg.exe。请在设置中选择 FFmpeg，或将其放入应用目录 tools/ffmpeg/win-x64/。");
        }

        if (!File.Exists(videoPath) || !File.Exists(audioPath))
        {
            return new FfmpegResult(false, -1, null, "FFmpeg 输入暂存文件不存在。");
        }

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

        using var process = new Process { StartInfo = startInfo };
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            if (!process.Start()) return new FfmpegResult(false, -1, null, "无法启动 ffmpeg.exe。");

            stdoutTask = CaptureTailAsync(process.StandardOutput);
            stderrTask = CaptureTailAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            progress?.Report(1);

            var success = process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            var diagnostic = string.IsNullOrWhiteSpace(error) ? output : error;
            return new FfmpegResult(success, process.ExitCode, success ? outputPath : null, success ? null : diagnostic.Trim());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception)
        {
            TryKill(process);
            if (stdoutTask is not null && stderrTask is not null)
            {
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original process failure.
                }
            }
            return new FfmpegResult(false, -1, null, exception.Message);
        }
    }

    public async Task<bool> TestAsync(CancellationToken cancellationToken = default)
    {
        var executable = FindFfmpeg();
        if (executable is null) return false;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.StartInfo.ArgumentList.Add("-version");
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            if (!process.Start()) return false;
            stdoutTask = CaptureTailAsync(process.StandardOutput);
            stderrTask = CaptureTailAsync(process.StandardError);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            return false;
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
        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            (Path.IsPathRooted(candidate) ? File.Exists(candidate) : IsOnPath(candidate!)));
    }

    private static bool IsOnPath(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(executable);
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            if (!process.WaitForExit(2000))
            {
                TryKill(process);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> CaptureTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder(MaxCapturedCharacters);
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            if (read >= MaxCapturedCharacters)
            {
                tail.Clear();
                tail.Append(buffer, read - MaxCapturedCharacters, MaxCapturedCharacters);
                continue;
            }

            var overflow = tail.Length + read - MaxCapturedCharacters;
            if (overflow > 0) tail.Remove(0, Math.Min(overflow, tail.Length));
            tail.Append(buffer, 0, read);
        }
        return tail.ToString();
    }

    private static bool IsStagingOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathRooted(outputPath)) return false;
        var fullPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetFileName(fullPath), "final-output.tmp", StringComparison.OrdinalIgnoreCase)) return false;
        var taskRoot = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(taskRoot) ||
            !Guid.TryParseExact(Path.GetFileName(taskRoot), "N", out _))
        {
            return false;
        }

        var stagingDirectory = Path.GetDirectoryName(taskRoot);
        return !string.IsNullOrWhiteSpace(stagingDirectory) &&
            string.Equals(Path.GetFileName(stagingDirectory), StagingDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the check and Kill.
        }
    }
}
