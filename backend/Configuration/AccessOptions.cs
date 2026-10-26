namespace ReVerse.Capture.Configuration;

public sealed class AccessOptions
{
    public const string HeaderName = "x-api-key";
    public string ApiKey { get; set; } = "";

    public bool Enabled => ApiKey.Length > 0;

    public void Validate()
    {
        ApiKey = ApiKey.Trim();
        if (ApiKey.Length is > 0 and < 16 || ApiKey.Length > 1024 || ApiKey.Any(char.IsControl))
            throw new ArgumentException("Access:ApiKey must be empty or 16–1024 characters without control characters.");
    }
}
