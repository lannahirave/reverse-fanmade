using System.Text.RegularExpressions;

namespace ReVerse.Capture.Configuration;

public sealed partial class AccessOptions
{
    public const int MaxKeyLength = 128;
    public const string HeaderName = "x-api-key";
    public string ApiKey { get; set; } = "";

    public bool Enabled => ApiKey.Length > 0;

    public void Validate()
    {
        ApiKey = ApiKey.Trim();
        if (ApiKey.Length > 0 && !KeyPattern().IsMatch(ApiKey))
            throw new ArgumentException("Access:ApiKey must be empty or 16–128 letters, digits, dots, dashes or underscores.");
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{16,128}\z")]
    private static partial Regex KeyPattern();
}
