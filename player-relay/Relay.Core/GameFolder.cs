using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ReVerse.Relay;

public static partial class GameFolder
{
    public const string SteamAppId = "1236300";
    private const string ProcessName = "Reverse";

    public static string? Find(string? configuredGameDirectory, IEnumerable<string> steamRoots)
    {
        if (!string.IsNullOrWhiteSpace(configuredGameDirectory) && Directory.Exists(configuredGameDirectory))
            return Path.GetFullPath(configuredGameDirectory);
        return FromRunningGame() ?? FromSteam(steamRoots);
    }

    public static string? FromRunningGame()
    {
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    var directory = Path.GetDirectoryName(process.MainModule?.FileName);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return null;
    }

    public static string? FromSteam(IEnumerable<string> steamRoots)
    {
        foreach (var library in Libraries(steamRoots))
        {
            var steamApps = Path.Combine(library, "steamapps");
            var manifest = Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf");
            if (!File.Exists(manifest)) continue;
            string text;
            try { text = File.ReadAllText(manifest); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var installDir = Values(text, "installdir").FirstOrDefault();
            if (string.IsNullOrWhiteSpace(installDir)) continue;
            var game = Path.Combine(steamApps, "common", installDir);
            if (Directory.Exists(game)) return Path.GetFullPath(game);
        }
        return null;
    }

    private static IEnumerable<string> Libraries(IEnumerable<string> steamRoots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            if (seen.Add(Path.GetFullPath(root))) yield return root;
            var folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(folders)) continue;
            string text;
            try { text = File.ReadAllText(folders); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var path in Values(text, "path"))
                if (Directory.Exists(path) && seen.Add(Path.GetFullPath(path))) yield return path;
        }
    }

    private static IEnumerable<string> Values(string text, string key) =>
        KeyValue().Matches(text)
            .Where(m => string.Equals(m.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups["value"].Value.Replace(@"\\", @"\"));

    [GeneratedRegex("\"(?<key>[^\"]+)\"\\s+\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex KeyValue();
}
