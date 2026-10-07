using System.Diagnostics;
using System.Text;
using NovaClip.Core;
using NovaClip.Infrastructure;

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
    public Task<bool> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => IsAvailable, cancellationToken);

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

        var result = await RunProcessAsync(executable,
            FfmpegCommandArguments.ForMp4Mux(videoPath, audioPath, outputPath), outputPath, cancellationToken).ConfigureAwait(false);
        if (result.Success) progress?.Report(1);
        return result;
    }

    public async Task<FfmpegResult> ConcatenateAsync(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segmentPaths);
        if (!IsStagingOutputPath(outputPath) || segmentPaths.Count is < 1 or > 1024)
            return new FfmpegResult(false, -1, null, "FFmpeg concat requires bounded inputs and a NovaClip staging output.");
        var executable = FindFfmpeg();
        if (executable is null)
            return new FfmpegResult(false, -1, null, "找不到 ffmpeg.exe。请先在设置中选择 FFmpeg。");

        var taskRoot = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var names = new List<string>(segmentPaths.Count);
        foreach (var path in segmentPaths)
        {
            var name = Path.GetFileName(path);
            // Only task-owned generated filenames enter the demuxer manifest. This
            // keeps safe=1 and avoids arbitrary paths, protocols or escaped directives.
            if (!Path.IsPathRooted(path) || !File.Exists(path) ||
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), taskRoot, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith("segment-", StringComparison.Ordinal) ||
                !name.EndsWith(".part", StringComparison.Ordinal) ||
                !int.TryParse(name.AsSpan(8, name.Length - 13), out var index) || index < 0 ||
                name != $"segment-{index:D4}.part" || names.Contains(name, StringComparer.Ordinal))
                return new FfmpegResult(false, -1, null, "FFmpeg concat inputs must be unique segments in the current task staging directory.");
            names.Add(name);
        }

        var manifestPath = Path.Combine(taskRoot, "concat-inputs.txt");
        try
        {
            var manifest = "ffconcat version 1.0\n" + string.Join('\n', names.Select(name => $"file '{name}'")) + "\n";
            await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return await RunProcessAsync(executable, FfmpegCommandArguments.ForMp4Concat(manifestPath, outputPath),
                outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(manifestPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                StartupDiagnostics.Warning("Could not remove FFmpeg concat manifest.", exception);
            }
        }
    }

    private static async Task<FfmpegResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (outputPath is not null) Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

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
            var success = process.ExitCode == 0 &&
                (outputPath is null || File.Exists(outputPath) && new FileInfo(outputPath).Length > 0);
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

        var result = await RunProcessAsync(executable, ["-version"], null, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    private string? FindFfmpeg()
    {
        var candidates = new[]
        {
            _settings.FfmpegPath,
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "win-x64", "ffmpeg.exe"),
            "ffmpeg.exe"
        };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) return candidate;
            }
            else if (FindOnPath(candidate) is { } located) return located;
        }
        return null;
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var path = Path.Combine(directory.Trim().Trim('"'), executable);
                if (Path.IsPathRooted(path) && File.Exists(path)) return path;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // An invalid PATH entry must not hide later usable entries.
            }
        }
        return null;
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
