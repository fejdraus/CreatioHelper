namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Configuration for cluster key auto-pairing.
/// When agents share the same cluster key, they automatically accept each other
/// using HMAC challenge-response (the raw key is never transmitted).
/// </summary>
public class ClusterKeyConfiguration
{
    public bool Enabled { get; set; }
    public string Key { get; set; } = "";
    public int ChallengeTimeoutSeconds { get; set; } = 30;
    public int MaxChallengesPerMinute { get; set; } = 10;
    public List<string> SeedAddresses { get; set; } = new();
    public bool ShareRoster { get; set; } = true;
    public int RosterSyncIntervalMinutes { get; set; } = 15;
    public int MaxJoinTargets { get; set; } = 256;
    public int InitialJoinDelaySeconds { get; set; } = 15;
    public string AdvertisedApiAddress { get; set; } = "";
}
