namespace ReVerse.Capture.Matchmaking;

public sealed class MatchmakingOptions
{

    public bool ExperimentalSessionProtocol { get; set; }
    public Dictionary<string, int> Rulesets { get; set; } = new(StringComparer.Ordinal);
    public int TicketLifetimeSeconds { get; set; } = 120;
    public int JoinLifetimeSeconds { get; set; } = 60;



    public bool IgnorePlayerAttributes { get; set; }
    public int PartialMatchWaitSeconds { get; set; }

    public void Validate()
    {
        if (TicketLifetimeSeconds is < 1 or > 3600 || JoinLifetimeSeconds is < 1 or > 600)
            throw new ArgumentException("Invalid matchmaking lifetime.");
        if (PartialMatchWaitSeconds is < 0 or > 600
            || PartialMatchWaitSeconds > 0 && PartialMatchWaitSeconds >= TicketLifetimeSeconds)
            throw new ArgumentException("PartialMatchWaitSeconds must be 0 (disabled) or shorter than TicketLifetimeSeconds.");
        if (Rulesets.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is < 2 or > 10))
            throw new ArgumentException("Each explicit matchmaking ruleset needs 2..10 players.");
    }
}
