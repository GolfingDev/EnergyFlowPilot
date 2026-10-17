namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one read-only Homematic climate reading of a single channel at one polling cycle.
/// </summary>
public sealed class HomematicClimateSample
{
    private readonly DateTimeOffset polledAtUtc;
    private readonly DateTimeOffset? lastChangedAtUtc;

    /// <summary>
    /// Gets the UTC time of the polling cycle. This is the time axis for analyses because
    /// Homematic datapoint timestamps only change when the value changes.
    /// </summary>
    public required DateTimeOffset PolledAtUtc
    {
        get => polledAtUtc;
        init => polledAtUtc = EnsureUtc(value, nameof(PolledAtUtc));
    }

    /// <summary>
    /// Gets the UTC time the primary datapoint last changed according to the CCU, if known.
    /// </summary>
    public DateTimeOffset? LastChangedAtUtc
    {
        get => lastChangedAtUtc;
        init => lastChangedAtUtc = value is null ? null : EnsureUtc(value.Value, nameof(LastChangedAtUtc));
    }

    /// <summary>
    /// Gets the hardware channel address (serial:index). It survives renames in the CCU WebUI.
    /// </summary>
    public required string ChannelAddress { get; init; }

    public required string DeviceType { get; init; }

    public required string DeviceName { get; init; }

    public required string ChannelName { get; init; }

    public required HomematicClimateSampleKind Kind { get; init; }

    public decimal? ActualTemperatureCelsius { get; init; }

    public decimal? SetPointTemperatureCelsius { get; init; }

    public HomematicSetPointMode? SetPointMode { get; init; }

    /// <summary>
    /// Gets the valve opening in percent (0-100), already normalized across device families.
    /// </summary>
    public decimal? ValveOpeningPercent { get; init; }

    public decimal? RelativeHumidityPercent { get; init; }

    public bool? IsWindowOpen { get; init; }

    public required bool IsReachable { get; init; }

    private static DateTimeOffset EnsureUtc(DateTimeOffset value, string propertyName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Zeitpunkt fuer Homematic-Klimadaten muss in UTC angegeben sein.", propertyName);
        }

        return value;
    }
}
