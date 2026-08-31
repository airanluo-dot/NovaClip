using System.Diagnostics;

namespace BiliNative.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (!options.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, out var pid) || !options.TryGetValue("source", out var source) || !options.TryGetValue("target", out var target)) return 2;
            WaitForProcess(pid);
            CopyDirectory(source, target);
            if (options.TryGetValue("restart", out var restart) && !string.IsNullOrWhiteSpace(restart) && File.Exists(restart)) Process.Start(new ProcessStartInfo(restart) { UseShellExecute = true });
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
            process.WaitForExit(60_000);
        }
        catch (ArgumentException)
        {
            // The app already exited.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
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
}
