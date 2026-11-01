namespace TibberVictronController.Business.Models;

/// <summary>
/// A planned round trip. Distance and duration are one way, as calculated from the route. A series repeats on the
/// weekdays in <see cref="RecurrenceDays"/> (bit 1 &lt;&lt; DayOfWeek) at the local departure time of the first trip.
/// </summary>
public sealed record EvTrip(
    int Id,
    DateTimeOffset DepartureAtUtc,
    DateTimeOffset? ReturnAtUtc,
    string Destination,
    string ResolvedAddress,
    double DistanceKm,
    double DurationMinutes,
    DateTimeOffset CreatedAtUtc,
    int RecurrenceDays = 0,
    DateTimeOffset? RecurrenceUntilUtc = null)
{
    public bool IsRecurring => RecurrenceDays != 0;

    public static int MaskOf(IEnumerable<DayOfWeek> days) => days.Aggregate(0, (mask, day) => mask | (1 << (int)day));

    public static IReadOnlyList<DayOfWeek> DaysOf(int mask) =>
        Enum.GetValues<DayOfWeek>().Where(day => (mask & (1 << (int)day)) != 0).ToArray();
}

public sealed record EvTripDraft(
    DateTimeOffset DepartureAtUtc,
    DateTimeOffset? ReturnAtUtc,
    string Destination,
    string ResolvedAddress,
    double Latitude,
    double Longitude,
    double DistanceKm,
    double DurationMinutes,
    int RecurrenceDays = 0,
    DateTimeOffset? RecurrenceUntilUtc = null);

public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>One-way route from home to a destination.</summary>
public sealed record RouteEstimate(string ResolvedAddress, GeoPoint Destination, double DistanceKm, double DurationMinutes);

/// <summary>Car and wallbox values the state-of-charge plan is based on.</summary>
public sealed record EvCarParameters(
    double UsableCapacityKwh,
    double ConsumptionKwhPer100Km,
    double MinimumSocPercent,
    double MarginPercent,
    double ChargePowerWatts);

/// <summary>Requirement for one trip: the state of charge the car needs when it leaves.</summary>
public sealed record EvTripRequirement(
    EvTrip Trip,
    DateTimeOffset ReturnAtUtc,
    bool ReturnIsAssumed,
    double EnergyKwh,
    double RequiredSocPercent,
    double ExpectedSocAtReturnPercent,
    DateTimeOffset? LatestChargeStartUtc,
    bool IsReachable,
    string? Note);

public sealed record EvSocPoint(DateTimeOffset AtUtc, double SocPercent);

/// <summary>
/// The minimal state-of-charge course: charged only as much as needed and as late as possible at full wallbox power.
/// Price-optimised charging (stage 4) may charge earlier, but never ends below this course.
/// </summary>
public sealed record EvTripPlan(
    DateTimeOffset CalculatedAtUtc,
    double? CurrentSocPercent,
    bool CurrentSocIsAssumed,
    IReadOnlyList<EvTripRequirement> Trips,
    IReadOnlyList<EvSocPoint> Course,
    IReadOnlyList<string> Warnings);
