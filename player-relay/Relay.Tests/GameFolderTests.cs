using ReVerse.Relay;

internal static class GameFolderTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "relay-game-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var steam = Path.Combine(root, "Steam");
            var library = Path.Combine(root, "Games Library");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            var game = Path.Combine(library, "steamapps", "common", "RE ReVerse");
            Directory.CreateDirectory(game);
            var escapedLibrary = library.Replace(@"\", @"\\");
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
                "libraryfolders"
                {
                    "0"
                    {
                        "path"      "{{steam.Replace(@"\", @"\\")}}"
                        "apps" { "228980" "1" }
                    }
                    "1"
                    {
                        "path"      "{{escapedLibrary}}"
                        "apps" { "{{GameFolder.SteamAppId}}" "1" }
                    }
                }
                """);
            Check(GameFolder.FromSteam([steam]) is null, "game folder is not reported before the app manifest exists");
            File.WriteAllText(Path.Combine(library, "steamapps", $"appmanifest_{GameFolder.SteamAppId}.acf"), $$"""
                "AppState"
                {
                    "appid"     "{{GameFolder.SteamAppId}}"
                    "name"      "RE ReVerse"
                    "installdir"        "RE ReVerse"
                }
                """);
            Check(string.Equals(GameFolder.FromSteam([steam]), Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase),
                "game folder is found in a secondary Steam library");
            Check(GameFolder.FromSteam([Path.Combine(root, "missing"), steam]) is not null, "missing Steam roots are skipped");
            Check(string.Equals(GameFolder.Find(library, [steam]), Path.GetFullPath(library), StringComparison.OrdinalIgnoreCase),
                "configured game folder takes priority");
            Check(GameFolder.Find(Path.Combine(root, "nope"), [Path.Combine(root, "missing")]) is null || GameFolder.FromRunningGame() is not null,
                "no game folder is reported when nothing is installed");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
