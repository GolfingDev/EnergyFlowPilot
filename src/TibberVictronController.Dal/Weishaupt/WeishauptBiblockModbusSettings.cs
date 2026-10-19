namespace TibberVictronController.Dal.Weishaupt;

public sealed class WeishauptBiblockModbusSettings
{
    public const int MinimumPollingIntervalSeconds = 60;

    public required bool IsEnabled { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required byte SlaveId { get; init; }
    public required int PollingIntervalSeconds { get; init; }
    public required int SampleRetentionDays { get; init; }

    public bool IsReadyForPolling => IsEnabled && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535;
}
