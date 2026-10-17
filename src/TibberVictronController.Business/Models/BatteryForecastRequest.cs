namespace TibberVictronController.Business.Models;

/// <summary>
/// Describes a forecast calculation, optionally anchored to the live site load of the current slot.
/// </summary>
public sealed class BatteryForecastRequest
{
    public required DateTimeOffset StartsAtUtc { get; init; }

    public required DateTimeOffset EndsAtUtc { get; init; }

    /// <summary>
    /// Gets the measured grid power without the Victron battery that the battery could cover now, in watts.
    /// </summary>
    /// <remarks>
    /// When set, the first slot uses this value instead of the consumption forecast. Unplanned loads such as an electric car
    /// are then judged by the optimizer: covered now or energy held for later, more expensive slots.
    /// </remarks>
    public decimal? CurrentNetLoadWatts { get; init; }
}
