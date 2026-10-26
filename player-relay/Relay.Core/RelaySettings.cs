using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReVerse.Relay;

public sealed class RelaySettings
{
    public string Username { get; set; } = "";
    public string BackendAddress { get; set; } = "";
    public string BackendFolder { get; set; } = "";
    public int BackendPlayers { get; set; } = 2;
    public int BackendFillWaitSeconds { get; set; } = 15;
    public bool BackendUdpLoggingAllowed { get; set; }
    public string LanguageCode { get; set; } = "en";
    public bool DetailedLogsEnabled { get; set; }
    public string GameDirectory { get; set; } = "";
    [JsonIgnore]
    public string SecretKey { get; set; } = "";
    [JsonIgnore]
    public string ServerApiKey { get; set; } = "";

    public Uri Validate()
    {
        Username = Username.Trim();
        BackendAddress = BackendAddress.Trim().TrimEnd('/');
        if (Username.Length is < 1 or > 64 || Username.Any(char.IsControl))
            throw new RelayException(RelayErrorCode.InvalidUsername);
        if (SecretKey.Length is < 1 or > 1024)
            throw new RelayException(RelayErrorCode.InvalidSecretKey);
        ServerApiKey = ServerApiKey.Trim();
        if (ServerApiKey.Length > 1024 || ServerApiKey.Any(char.IsControl))
            throw new RelayException(RelayErrorCode.InvalidServerApiKey);
        if (!Uri.TryCreate(BackendAddress, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" ||
            uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
            throw new RelayException(RelayErrorCode.InvalidBackendUrl);
        return uri;
    }

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReVerseRelay");

    public static string DetailedLogsControlFile => Path.Combine(DataDirectory, "detailed-logs.enabled");

    public static string DetailedLogsDirectory => Path.Combine(AppContext.BaseDirectory, "detailed-logs");

    public static RelaySettings Load()
    {
        var path = Path.Combine(DataDirectory, "settings.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<RelaySettings>(File.ReadAllText(path))
                ?? throw new RelayException(RelayErrorCode.SavedSettingsEmpty)
            : new RelaySettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
