using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

public sealed record EvTripPlanInput(
    DateTimeOffset NowUtc,
    double? CurrentSocPercent,
    IReadOnlyList<EvTrip> Trips,
    EvCarParameters Car,
    TimeZoneInfo LocalZone)
{
    /// <summary>Hourly temperature forecast; null or empty means no winter surcharge.</summary>
    public IReadOnlyList<HourlyTemperature>? Temperatures { get; init; }

    /// <summary>Extra consumption per kelvin below 15 °C (e.g. 1.2 means +18 % at 0 °C).</summary>
    public double WinterSurchargePerKelvinPercent { get; init; }
}

/// <summary>
/// Derives the state of charge each planned trip needs and the minimal course to get there.
/// Trip energy = outbound and return distance x consumption, plus the configured margin. When the car comes back it
/// must still hold the minimum state of charge. Between two trips the car can charge at full wallbox power while it
/// is at home, so an early trip only has to carry what a later trip cannot get from that window.
/// </summary>
public static class EvTripSocPlanner
{
    // AC charging losses between wallbox meter and battery (charger, battery heating); an assumption, not measured.
    public const double ChargeEfficiency = 0.9;
    // Without an entered return the car is assumed back at 20:00 local time on the departure day.
    private static readonly TimeSpan DefaultReturnLocalTime = TimeSpan.FromHours(20);
    // Battery and cabin heating raise consumption below roughly 15 °C; the surcharge is capped.
    private const double WinterBaseCelsius = 15;
    private const double MaximumWinterSurchargePercent = 40;

    public static EvTripPlan Plan(EvTripPlanInput input)
    {
        var car = input.Car;
        var capacity = car.UsableCapacityKwh;
        var reserve = capacity * car.MinimumSocPercent / 100d;
        var batteryPowerKw = car.ChargePowerWatts / 1000d * ChargeEfficiency;
        var trips = input.Trips.Where(trip => trip.DepartureAtUtc > input.NowUtc).OrderBy(trip => trip.DepartureAtUtc).ToList();
        var warnings = new List<string>();
        var returns = trips.Select(trip => ReturnOf(trip, input.LocalZone)).ToList();
        var temperatures = trips.Select((trip, index) => AverageTemperature(input.Temperatures, trip.DepartureAtUtc, returns[index].At)).ToList();
        var winter = temperatures.Select(temperature => temperature is { } celsius
            ? Math.Clamp((WinterBaseCelsius - celsius) * input.WinterSurchargePerKelvinPercent, 0, MaximumWinterSurchargePercent)
            : 0).ToList();
        var energy = trips.Select((trip, index) => 2 * trip.DistanceKm * car.ConsumptionKwhPer100Km / 100d
            * (1 + car.MarginPercent / 100d) * (1 + winter[index] / 100d)).ToList();
        if (trips.Count > 0 && input.WinterSurchargePerKelvinPercent > 0 && temperatures.Any(temperature => temperature is null))
            warnings.Add("Für einzelne Fahrten fehlt die Temperaturprognose; dort ohne Winterzuschlag gerechnet.");

        for (var i = 0; i + 1 < trips.Count; i++)
        {
            if (returns[i].At <= trips[i + 1].DepartureAtUtc) continue;
            warnings.Add($"Die Rückkehr der Fahrt nach {trips[i].Destination} liegt nach der Abfahrt nach {trips[i + 1].Destination}; dazwischen wird nicht geladen.");
            returns[i] = (trips[i + 1].DepartureAtUtc, returns[i].Assumed);
        }

        // Backward pass: what must be in the battery at each departure so that all later trips still work.
        var need = new double[trips.Count];
        for (var i = trips.Count - 1; i >= 0; i--)
        {
            var afterReturn = i == trips.Count - 1
                ? reserve
                : Math.Max(reserve, need[i + 1] - Hours(returns[i].At, trips[i + 1].DepartureAtUtc) * batteryPowerKw);
            need[i] = energy[i] + afterReturn;
        }

        // Forward pass: charge only what is needed, as late as possible, starting from the current state of charge.
        var socAssumed = input.CurrentSocPercent is null;
        var level = (input.CurrentSocPercent ?? car.MinimumSocPercent) / 100d * capacity;
        var time = input.NowUtc;
        var course = new List<EvSocPoint> { new(time, Percent(level, capacity)) };
        var requirements = new List<EvTripRequirement>();
        for (var i = 0; i < trips.Count; i++)
        {
            var trip = trips[i];
            var target = Math.Min(need[i], capacity);
            var reachable = need[i] <= capacity;
            string? note = reachable ? null : $"Braucht {Percent(need[i], capacity):0} %, mehr als ein voller Akku: unterwegs laden.";
            DateTimeOffset? latestStart = null;
            if (target > level)
            {
                var start = trip.DepartureAtUtc - TimeSpan.FromHours((target - level) / batteryPowerKw);
                if (start < time)
                {
                    reachable = false;
                    var reached = level + Hours(time, trip.DepartureAtUtc) * batteryPowerKw;
                    note ??= $"Bis zur Abfahrt sind auch mit voller Ladeleistung nur {Percent(reached, capacity):0} % erreichbar.";
                    start = time;
                    level = reached;
                }
                else
                {
                    course.Add(new EvSocPoint(start, Percent(level, capacity)));
                    level = target;
                }

                latestStart = start;
            }

            course.Add(new EvSocPoint(trip.DepartureAtUtc, Percent(level, capacity)));
            var afterTrip = level - energy[i];
            course.Add(new EvSocPoint(returns[i].At, Percent(Math.Max(0, afterTrip), capacity)));
            requirements.Add(new EvTripRequirement(trip, returns[i].At, returns[i].Assumed, energy[i], Percent(need[i], capacity),
                Percent(afterTrip, capacity), latestStart, reachable, note, winter[i], temperatures[i]));
            level = Math.Max(0, afterTrip);
            time = returns[i].At;
        }

        if (socAssumed && trips.Count > 0)
            warnings.Add("Der aktuelle Ladestand ist unbekannt; der Verlauf beginnt beim Mindest-Ladestand.");
        return new EvTripPlan(input.NowUtc, input.CurrentSocPercent, socAssumed, requirements, course, warnings);
    }

    // Mean of the forecast hours between departure and return; the nearest hour within three hours for short trips.
    private static double? AverageTemperature(IReadOnlyList<HourlyTemperature>? temperatures, DateTimeOffset from, DateTimeOffset to)
    {
        if (temperatures is null || temperatures.Count == 0) return null;
        var inside = temperatures.Where(hour => hour.StartsAtUtc >= from.AddHours(-1) && hour.StartsAtUtc < to).ToArray();
        if (inside.Length > 0) return (double)inside.Average(hour => hour.TemperatureCelsius);
        var nearest = temperatures.MinBy(hour => (hour.StartsAtUtc - from).Duration());
        return nearest is not null && (nearest.StartsAtUtc - from).Duration() <= TimeSpan.FromHours(3) ? (double)nearest.TemperatureCelsius : null;
    }

    private static (DateTimeOffset At, bool Assumed) ReturnOf(EvTrip trip, TimeZoneInfo zone)
    {
        if (trip.ReturnAtUtc is { } entered) return (entered, false);
        var departureLocal = TimeZoneInfo.ConvertTime(trip.DepartureAtUtc, zone);
        var eveningLocal = departureLocal.Date + DefaultReturnLocalTime;
        var evening = new DateTimeOffset(eveningLocal, zone.GetUtcOffset(eveningLocal)).ToUniversalTime();
        // Never earlier than driving there and back.
        var earliest = trip.DepartureAtUtc + TimeSpan.FromMinutes(2 * trip.DurationMinutes);
        return (evening > earliest ? evening : earliest, true);
    }

    private static double Hours(DateTimeOffset from, DateTimeOffset to) => Math.Max(0, (to - from).TotalHours);

    private static double Percent(double kwh, double capacity) => kwh / capacity * 100d;
}
