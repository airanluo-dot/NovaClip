using System.Diagnostics;

namespace NovaClip.Updater;

internal static class Program
{
    private const int Success = 0;
    private const int InvalidArguments = 2;
    private const int InvalidPath = 3;
    private const int BootstrapFailed = 4;
    private const int UpdateFailed = 6;

    private static int Main(string[] args)
    {
        try
        {
            if (!UpdaterOptions.TryParse(args, out var options)) return InvalidArguments;
            return Run(options);
        }
        catch (Exception exception)
        {
            try { Console.Error.WriteLine("NovaClip updater failed: " + exception.Message); } catch { }
            return 1;
        }
    }

    private static int Run(UpdaterOptions options)
    {
        if (!options.Bootstrap) return StartBootstrap(options);
        if (!WaitForProcess(options.ProcessId)) return UpdateFailed;

        if (options.InstallerPath is not null)
        {
            if (!IsSafeExistingFile(options.InstallerPath, ".exe") || !IsSafeDirectory(options.TargetDirectory)) return InvalidPath;
            return RunInstaller(options);
        }

        if (options.SourceDirectory is null ||
            !IsSafeDirectory(options.SourceDirectory) ||
            !IsSafeDirectory(options.TargetDirectory) ||
            string.Equals(Normalize(options.SourceDirectory), Normalize(options.TargetDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return InvalidPath;
        }

        PortableUpdateTransaction? transaction = null;
        try
        {
            transaction = PortableUpdateTransaction.Begin(options.SourceDirectory, options.TargetDirectory);
            if (!StartAndCheck(options.RestartPath))
            {
                transaction.Rollback();
                return UpdateFailed;
            }

            transaction.Commit();
            return Success;
        }
        catch
        {
            try { transaction?.Rollback(); } catch { }
            return UpdateFailed;
        }
    }

    private static int StartBootstrap(UpdaterOptions options)
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath)) return BootstrapFailed;

        var bootstrapDirectory = Path.Combine(Path.GetTempPath(), "NovaClip", "updater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bootstrapDirectory);
        var bootstrapPath = Path.Combine(bootstrapDirectory, "NovaClip.Updater.bootstrap.exe");
        File.Copy(currentPath, bootstrapPath, overwrite: false);

        var startInfo = new ProcessStartInfo(bootstrapPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in options.ToArguments(includeBootstrap: true)) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) is null ? BootstrapFailed : Success;
    }

    private static int RunInstaller(UpdaterOptions options)
    {
        var startInfo = new ProcessStartInfo(options.InstallerPath!)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(options.InstallerPath!)!
        };
        startInfo.ArgumentList.Add("/VERYSILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/DIR=" + options.TargetDirectory);

        using var installer = Process.Start(startInfo);
        if (installer is null || !installer.WaitForExit(300_000)) return UpdateFailed;
        if (installer.ExitCode != 0) return installer.ExitCode;
        return StartAndCheck(options.RestartPath) ? Success : UpdateFailed;
    }

    private static bool WaitForProcess(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.Id == Environment.ProcessId) return false;
            return process.WaitForExit(60_000);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartAndCheck(string path)
    {
        if (!IsSafeExistingFile(path, ".exe")) return false;
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        if (process is null) return false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (process.HasExited) return false;
            Thread.Sleep(500);
        }

        return true;
    }

    private static bool IsSafeDirectory(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathRooted(path) &&
        Directory.Exists(path) &&
        !HasReparsePoint(path);

    private static bool IsSafeExistingFile(string path, string extension) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathRooted(path) &&
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path) &&
        !HasReparsePoint(path);

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class UpdaterOptions
    {
        private static readonly HashSet<string> ValueKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "pid", "source", "target", "installer", "restart"
        };

        public int ProcessId { get; private init; }
        public string? SourceDirectory { get; private init; }
        public string? TargetDirectory { get; private init; }
        public string? InstallerPath { get; private init; }
        public string RestartPath { get; private init; } = string.Empty;
        public bool Bootstrap { get; private init; }

        public static bool TryParse(string[] args, out UpdaterOptions options)
        {
            options = new UpdaterOptions();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var bootstrap = false;
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (string.Equals(argument, "--bootstrap", StringComparison.OrdinalIgnoreCase))
                {
                    if (bootstrap) return false;
                    bootstrap = true;
                    continue;
                }
                if (argument.Length <= 2 || !argument.StartsWith("--", StringComparison.Ordinal) || !ValueKeys.Contains(argument[2..]) || index + 1 >= args.Length)
                {
                    return false;
                }
                var key = argument[2..];
                var value = args[++index];
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(key, value)) return false;
            }

            if (!values.TryGetValue("pid", out var pidText) ||
                !int.TryParse(pidText, out var pid) ||
                pid <= 0 ||
                !values.TryGetValue("target", out var target) ||
                !values.TryGetValue("restart", out var restart) ||
                (values.ContainsKey("source") == values.ContainsKey("installer")))
            {
                return false;
            }

            options = new UpdaterOptions
            {
                ProcessId = pid,
                SourceDirectory = values.GetValueOrDefault("source"),
                TargetDirectory = target,
                InstallerPath = values.GetValueOrDefault("installer"),
                RestartPath = restart,
                Bootstrap = bootstrap
            };
            return true;
        }

        public IEnumerable<string> ToArguments(bool includeBootstrap)
        {
            if (includeBootstrap) yield return "--bootstrap";
            yield return "--pid";
            yield return ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (SourceDirectory is not null)
            {
                yield return "--source";
                yield return SourceDirectory;
            }
            if (InstallerPath is not null)
            {
                yield return "--installer";
                yield return InstallerPath;
            }
            yield return "--target";
            yield return TargetDirectory!;
            yield return "--restart";
            yield return RestartPath;
        }
    }
}
