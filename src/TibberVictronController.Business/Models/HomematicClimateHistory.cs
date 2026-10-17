namespace TibberVictronController.Business.Models;

/// <summary>
/// Selectable history window for the heating view.
/// </summary>
public enum HomematicHistoryRange
{
    Day,
    Week,
    Month
}

/// <summary>
/// Bucketed Homematic climate history prepared for charts, plus the current heating state.
/// </summary>
public sealed class HomematicClimateHistory
{
    public required DateTimeOffset StartsAtUtc { get; init; }

    public required DateTimeOffset EndsAtUtc { get; init; }

    public required TimeSpan BucketDuration { get; init; }

    public required bool IsCollectionEnabled { get; init; }

    public DateTimeOffset? LastPolledAtUtc { get; init; }

    public required HomematicClimateSummary Summary { get; init; }

    public required IReadOnlyList<HomematicClimateSeries> Series { get; init; }

    /// <summary>
    /// Gets per-bucket pairs of outdoor temperature and mean valve opening per heating circuit,
    /// used to judge whether the heating curve delivers too much or too little.
    /// </summary>
    public required IReadOnlyList<HomematicValveOutdoorPoint> ValveVersusOutdoor { get; init; }
}

/// <summary>
/// Heating state of the latest polling cycle.
/// </summary>
public sealed class HomematicClimateSummary
{
    public decimal? OutdoorTemperatureCelsius { get; init; }

    /// <summary>
    /// Gets the mean valve opening of single radiator thermostats (heating circuit with radiators).
    /// </summary>
    public decimal? RadiatorValveOpeningPercent { get; init; }

    /// <summary>
    /// Gets the mean valve opening of underfloor heating zones (heating circuit with floor heating).
    /// </summary>
    public decimal? UnderfloorValveOpeningPercent { get; init; }

    public required int RoomCount { get; init; }

    public required int RoomsBelowSetPointCount { get; init; }
}

/// <summary>
/// History of one Homematic channel.
/// </summary>
public sealed class HomematicClimateSeries
{
    public required string ChannelAddress { get; init; }

    public required string DisplayName { get; init; }

    public required HomematicClimateSampleKind Kind { get; init; }

    /// <summary>
    /// Gets whether this channel represents a room on its own, so rooms are not shown or counted twice
    /// (group, radiator and wall thermostat of the same room).
    /// </summary>
    public required bool IsRoom { get; init; }

    public required IReadOnlyList<HomematicClimatePoint> Points { get; init; }
}

public sealed class HomematicClimatePoint
{
    public required DateTimeOffset BucketStartsAtUtc { get; init; }

    public decimal? ActualTemperatureCelsius { get; init; }

    public decimal? SetPointTemperatureCelsius { get; init; }

    public decimal? ValveOpeningPercent { get; init; }
}

public sealed class HomematicValveOutdoorPoint
{
    public required DateTimeOffset BucketStartsAtUtc { get; init; }

    public required decimal OutdoorTemperatureCelsius { get; init; }

    public decimal? RadiatorValveOpeningPercent { get; init; }

    public decimal? UnderfloorValveOpeningPercent { get; init; }
}
