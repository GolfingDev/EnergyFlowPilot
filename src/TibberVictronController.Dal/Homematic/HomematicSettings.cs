namespace TibberVictronController.Dal.Homematic;

public sealed class HomematicSettings
{
    public const int MinimumPollingIntervalSeconds = 60;

    public required bool IsEnabled { get; init; }
    public required string BaseUrl { get; init; }
    public string? ApiToken { get; init; }
    public required int PollingIntervalSeconds { get; init; }
    public required int SampleRetentionDays { get; init; }

    /// <summary>
    /// The XML-API answers with HTTP 200 and an empty "not_authenticated" body without a token,
    /// so polling only makes sense when enabled, addressed and authorized.
    /// </summary>
    public bool IsReadyForPolling =>
        IsEnabled &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiToken);
}
