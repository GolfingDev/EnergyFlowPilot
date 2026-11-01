using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

public sealed record EvTripPlanningDependencies(
    IEvTripRepository Trips,
    IRoutePlanner Routes,
    IControllerSettingStore Settings,
    ICarStatusSource Car,
    IUtcClock Clock,
    ITemperatureProvider? Temperatures = null,
    ICarSampleRepository? CarSamples = null);

/// <summary>A trip to add; with weekdays it is a weekly series starting at the departure, optionally until a date.</summary>
public sealed record EvTripRequest(
    DateTimeOffset DepartureAtUtc,
    DateTimeOffset? ReturnAtUtc,
    string Destination,
    IReadOnlyCollection<DayOfWeek>? RecurrenceDays = null,
    DateTimeOffset? RecurrenceUntilUtc = null);

/// <summary>The plan plus the calendar trips the operator has hidden (shown so they can be brought back).</summary>
public sealed record EvTripPlanView(EvTripPlan Plan, EvCarParameters Car, IReadOnlyList<EvTrip> IgnoredTrips)
{
    /// <summary>Consumption learned from the car's drives; used instead of the setting when switched on.</summary>
    public LearnedConsumption? LearnedConsumption { get; init; }

    public bool UsesLearnedConsumption { get; init; }
}

/// <summary>Manual trip planning: stores trips with their calculated route and derives the required state of charge.</summary>
public sealed class EvTripPlanningService(EvTripPlanningDependencies dependencies)
{
    private static readonly TimeZoneInfo LocalZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    // Series are shown and planned two weeks ahead; later occurrences follow as time moves on.
    public static readonly TimeSpan SeriesHorizon = TimeSpan.FromDays(14);

    public async Task<EvTripPlanView> GetPlanAsync(CancellationToken cancellationToken)
    {
        var now = dependencies.Clock.UtcNow;
        var car = await ReadCarParametersAsync(cancellationToken);
        var learned = await LearnConsumptionAsync(car, now, cancellationToken);
        var useLearned = learned is not null && await FlagAsync(ControllerSettingDefaults.EvCarUseLearnedConsumptionKey, cancellationToken);
        if (useLearned) car = car with { ConsumptionKwhPer100Km = learned!.KwhPer100Km };
        var trips = EvTripOccurrences.Expand(await dependencies.Trips.GetUpcomingAsync(now, cancellationToken), now, SeriesHorizon, LocalZone);
        var active = trips.Where(trip => !trip.IsIgnored).ToArray();
        var plan = EvTripSocPlanner.Plan(new EvTripPlanInput(now, dependencies.Car.Car.StateOfChargePercent, active, car, LocalZone)
        {
            Temperatures = active.Length == 0 ? null : await TemperaturesAsync(now, cancellationToken),
            WinterSurchargePerKelvinPercent = await NumberAsync(ControllerSettingDefaults.EvTripWinterSurchargeKey, cancellationToken)
        });
        return new EvTripPlanView(plan, car, trips.Where(trip => trip.IsIgnored && trip.DepartureAtUtc > now).ToArray())
        {
            LearnedConsumption = learned,
            UsesLearnedConsumption = useLearned
        };
    }

    public async Task<EvTrip> AddTripAsync(EvTripRequest request, CancellationToken cancellationToken)
    {
        var now = dependencies.Clock.UtcNow;
        var destination = request.Destination?.Trim() ?? string.Empty;
        if (destination.Length is < 2 or > 200)
            throw new ArgumentException("Das Ziel muss zwischen 2 und 200 Zeichen lang sein.");
        if (request.DepartureAtUtc < now.AddMinutes(-5))
            throw new ArgumentException("Die Abfahrt liegt in der Vergangenheit.");
        if (request.DepartureAtUtc > now.AddYears(1))
            throw new ArgumentException("Fahrten koennen hoechstens ein Jahr im Voraus geplant werden.");
        if (request.ReturnAtUtc is { } returnAt && returnAt <= request.DepartureAtUtc)
            throw new ArgumentException("Die Rueckkehr muss nach der Abfahrt liegen.");
        var recurrence = EvTrip.MaskOf(request.RecurrenceDays ?? []);
        if (recurrence != 0)
        {
            // Each occurrence takes the return time of the first trip; a longer trip would overlap the next one.
            if (request.ReturnAtUtc is { } seriesReturn && seriesReturn - request.DepartureAtUtc >= TimeSpan.FromDays(1))
                throw new ArgumentException("Bei Serienfahrten muss die Rueckkehr innerhalb von 24 Stunden nach der Abfahrt liegen.");
            if (request.RecurrenceUntilUtc is { } until && until < request.DepartureAtUtc)
                throw new ArgumentException("Das Ende der Serie liegt vor der ersten Abfahrt.");
        }
        else if (request.RecurrenceUntilUtc is not null)
        {
            throw new ArgumentException("Ein Serienende ist nur mit ausgewaehlten Wochentagen moeglich.");
        }

        var origin = await ReadHomeAsync(cancellationToken);
        var route = await dependencies.Routes.EstimateAsync(origin, destination, false, cancellationToken);
        return await dependencies.Trips.AddAsync(new EvTripDraft(request.DepartureAtUtc.ToUniversalTime(), request.ReturnAtUtc?.ToUniversalTime(),
            destination, route.ResolvedAddress, route.Destination.Latitude, route.Destination.Longitude, route.DistanceKm, route.DurationMinutes,
            recurrence, recurrence == 0 ? null : request.RecurrenceUntilUtc?.ToUniversalTime()),
            now, cancellationToken);
    }

    /// <summary>A calendar trip is only hidden: deleting it would bring it back with the next calendar sync.</summary>
    public async Task<bool> DeleteTripAsync(int id, CancellationToken cancellationToken)
    {
        var trip = await dependencies.Trips.GetAsync(id, cancellationToken);
        if (trip is null) return false;
        return trip.IsFromCalendar
            ? await dependencies.Trips.SetIgnoredAsync(id, true, cancellationToken)
            : await dependencies.Trips.DeleteAsync(id, cancellationToken);
    }

    public Task<bool> SetIgnoredAsync(int id, bool ignored, CancellationToken cancellationToken) =>
        dependencies.Trips.SetIgnoredAsync(id, ignored, cancellationToken);

    /// <summary>Skips (or restores) one date of a weekly series, e.g. during holidays.</summary>
    public Task<bool> SetOccurrenceSkippedAsync(int id, DateOnly date, bool skipped, CancellationToken cancellationToken) =>
        dependencies.Trips.SetOccurrenceSkippedAsync(id, date, skipped, cancellationToken);

    // Learning works on the drives of the last four months; a failure just keeps the configured consumption.
    private async Task<LearnedConsumption?> LearnConsumptionAsync(EvCarParameters car, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (dependencies.CarSamples is null) return null;
        try
        {
            var samples = await dependencies.CarSamples.GetSinceAsync(now.AddDays(-120), cancellationToken);
            if (samples.Count < 2) return null;
            IReadOnlyList<HourlyTemperature>? temperatures = null;
            if (dependencies.Temperatures is not null)
            {
                try
                {
                    temperatures = await dependencies.Temperatures.GetHourlyTemperaturesAsync(samples[0].MeasuredAtUtc.AddHours(-1), now, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Without past temperatures the drives are not normalised; still better than nothing.
                }
            }

            return CarConsumptionLearner.Learn(samples, car.UsableCapacityKwh, temperatures,
                await NumberAsync(ControllerSettingDefaults.EvTripWinterSurchargeKey, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<bool> FlagAsync(string key, CancellationToken cancellationToken) =>
        bool.TryParse((await dependencies.Settings.GetSettingAsync(key, cancellationToken))?.Value, out var value) && value;

    // A missing forecast only removes the winter surcharge; it never blocks the plan.
    private async Task<IReadOnlyList<HourlyTemperature>?> TemperaturesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (dependencies.Temperatures is null) return null;
        try
        {
            return await dependencies.Temperatures.GetHourlyTemperaturesAsync(now.AddHours(-1), now + SeriesHorizon + TimeSpan.FromDays(1), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<EvCarParameters> ReadCarParametersAsync(CancellationToken cancellationToken) => new(
        await NumberAsync(ControllerSettingDefaults.EvCarUsableCapacityKwhKey, cancellationToken),
        await NumberAsync(ControllerSettingDefaults.EvCarConsumptionKey, cancellationToken),
        await NumberAsync(ControllerSettingDefaults.EvCarMinimumSocKey, cancellationToken),
        await NumberAsync(ControllerSettingDefaults.EvTripMarginKey, cancellationToken),
        await NumberAsync(ControllerSettingDefaults.EvWallboxMaximumPowerKey, cancellationToken));

    // Home is the installation location already maintained for the PV forecast.
    private async Task<GeoPoint> ReadHomeAsync(CancellationToken cancellationToken) => new(
        await NumberAsync(ControllerSettingDefaults.PvForecastLatitudeKey, cancellationToken),
        await NumberAsync(ControllerSettingDefaults.PvForecastLongitudeKey, cancellationToken));

    private async Task<double> NumberAsync(string key, CancellationToken cancellationToken)
    {
        var value = (await dependencies.Settings.GetSettingAsync(key, cancellationToken))?.Value;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new InvalidOperationException($"Die Einstellung {key} fehlt oder ist keine Zahl.");
    }
}
