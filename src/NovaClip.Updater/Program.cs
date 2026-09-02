using System.Diagnostics;

namespace NovaClip.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (!options.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, out var pid) || pid <= 0) return 2;
            WaitForProcess(pid);

            if (options.TryGetValue("installer", out var installer))
            {
                if (!IsSafeExistingFile(installer, ".exe")) return 3;
                var setupInfo = new ProcessStartInfo(installer)
                {
                    UseShellExecute = true
                };
                setupInfo.ArgumentList.Add("/VERYSILENT");
                setupInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
                setupInfo.ArgumentList.Add("/NORESTART");
                if (options.TryGetValue("target", out var installerTarget) && IsSafeDirectory(installerTarget)) setupInfo.ArgumentList.Add($"/DIR={installerTarget}");
                using var setup = Process.Start(setupInfo);
                if (setup is null) return 4;
                if (!setup.WaitForExit(300_000))
                {
                    try { if (!setup.HasExited) setup.Kill(true); } catch { }
                    return 5;
                }
                if (setup.ExitCode != 0) return setup.ExitCode;
            }
            else
            {
                if (!options.TryGetValue("source", out var source) || !options.TryGetValue("target", out var target)) return 2;
                if (!IsSafeDirectory(source) || !IsSafeDirectory(target)) return 3;
                CopyDirectory(source, target);
            }

            if (options.TryGetValue("restart", out var restart) && IsSafeExistingFile(restart, ".exe"))
            {
                Process.Start(new ProcessStartInfo(restart) { UseShellExecute = true });
            }
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) result[args[i][2..]] = args[++i];
        }
        return result;
    }

    private static void WaitForProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("NovaClip", StringComparison.OrdinalIgnoreCase)) return;
            if (!process.WaitForExit(60_000)) throw new TimeoutException("NovaClip did not exit before the update timeout.");
        }
        catch (ArgumentException)
        {
            // The app already exited.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The update source and target directories must differ.");
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (string.Equals(Path.GetFileName(file), "NovaClip.Updater.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static bool IsSafeDirectory(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Directory.Exists(path);

    private static bool IsSafeExistingFile(string path, string extension) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}
