using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>
/// One quarter hour offered to the car: grid price, PV that would otherwise be fed in, and PV the home battery plan
/// would store (the car may take it at what that energy would save in the home battery).
/// </summary>
public sealed record EvChargeSlotInput(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, double PricePerKwh, bool IsPriceEstimated, double SurplusKwh)
{
    public double BatteryPvKwh { get; init; }

    public double BatteryPvValuePerKwh { get; init; }
}

public sealed record EvChargePlanInput(
    DateTimeOffset NowUtc,
    double? CurrentSocPercent,
    double? CarTargetSocPercent,
    EvCarParameters Car,
    double BaseSocPercent,
    double FeedInPricePerKwh,
    IReadOnlyList<EvChargeSlotInput> Slots,
    IReadOnlyList<EvTripRequirement> Trips)
{
    /// <summary>
    /// Expected later grid price for energy left in the car at the end of the horizon; null uses 85 % of the median
    /// slot price. Charging pays off when it is cheaper than this, so the car fills up in cheap hours without a trip.
    /// </summary>
    public double? FutureValuePerKwh { get; init; }

    /// <summary>Share of the PV forecast the plan relies on; trips must work even when the sun disappoints.</summary>
    public double PvConfidence { get; init; } = 0.7;
}

public enum EvChargeReason
{
    Trip,
    PvSurplus,
    BaseSoc,
    CheapPrice
}

public sealed record EvPlannedChargeSlot(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    double PowerWatts,
    double GridKwh,
    double PvKwh,
    double PricePerKwh,
    bool IsPriceEstimated,
    EvChargeReason Reason);

/// <summary>Grid energy and cost of the chosen plan if the PV turns out lower or higher than forecast.</summary>
public sealed record EvChargeScenario(string Name, double PvFactor, double GridKwh, double GridCost);

public sealed record EvChargePlan(
    DateTimeOffset CalculatedAtUtc,
    IReadOnlyList<EvPlannedChargeSlot> Slots,
    double GridKwh,
    double PvKwh,
    double GridCost,
    IReadOnlyList<string> Warnings,
    bool HasInputSlots = true)
{
    public IReadOnlyList<EvChargeScenario> Scenarios { get; init; } = [];
}

/// <summary>
/// Cost-optimal charging plan for the car: dynamic programming over the car's battery energy per quarter hour.
/// Each kWh at the wallbox comes from the cheapest source of its slot: PV that would be fed in (feed-in price), PV the
/// home batteries would store (their marginal value, so PV goes where it saves more), then the grid. Departures are
/// hard requirements (the car must come back with at least the minimum state of charge); the car is away between
/// departure and return. Energy left at the end is worth its expected later price, so cheap hours are used beyond
/// the trips; below the base state of charge the car charges at once. The wallbox charges with 0 or 6 to 16 A on three phases.
/// </summary>
public static class EvChargePlanner
{
    // Halo: 6 A x 230 V x 3 phases, no single-phase switching.
    public const double MinimumChargePowerWatts = 4140;
    private const double WattsPerAmp = 690;
    // Missing energy at a departure must never be "cheaper" than any charging; below the base state of charge is
    // strongly, but not absolutely, avoided.
    private const double TripShortfallPenaltyPerKwh = 10;
    private const double BaseShortfallPenaltyPerKwh = 1;
    // Below the base state of charge every hour at home counts against the plan (operator 2026-11-03: "30 % is always
    // the minimum", so a car at 26 % charges now, not in the cheapest hour of the night). Far above any price difference,
    // so the car charges at full power until the base is reached.
    private const double BelowBasePenaltyPerKwhHour = 5;
    private const double EstimatedPriceHurdlePerKwh = 0.02;
    private const double FutureValueShare = 0.85;
    private static readonly (string Name, double Factor)[] ScenarioFactors = [("wenig PV", 0.5), ("erwartete PV", 1.0), ("viel PV", 1.3)];

    public static EvChargePlan Plan(EvChargePlanInput input)
    {
        var car = input.Car;
        var efficiency = EvTripSocPlanner.ChargeEfficiency;
        var warnings = new List<string>();
        var slots = input.Slots.Where(slot => slot.EndsAtUtc > input.NowUtc).OrderBy(slot => slot.StartsAtUtc).ToArray();
        // Without the car's state of charge, filling up "on suspicion" could charge a full car; only trips are planned,
        // cautiously from the minimum state of charge.
        var socKnown = input.CurrentSocPercent is not null;
        if (!socKnown)
            warnings.Add("Ladestand des Autos unbekannt: eingeplant werden nur Fahrten (vorsichtig ab dem Mindest-Ladestand), kein Auffüllen.");
        if (slots.Length == 0)
        {
            warnings.Add("Keine Preise und kein Akkuplan vorhanden; ein Ladeplan ist erst damit möglich.");
            return new EvChargePlan(input.NowUtc, [], 0, 0, 0, warnings, HasInputSlots: false);
        }

        var capacity = car.UsableCapacityKwh;
        var limit = Math.Min(input.CarTargetSocPercent ?? 100, 100) / 100d * capacity;
        var reserve = car.MinimumSocPercent / 100d * capacity;
        var baseEnergy = socKnown ? Math.Min(input.BaseSocPercent / 100d * capacity, limit) : 0;
        var start = Math.Clamp((input.CurrentSocPercent ?? car.MinimumSocPercent) / 100d * capacity, 0, capacity);
        var hours = slots.Select(slot => AvailableHours(slot, input.NowUtc, input.Trips)).ToArray();
        var departures = Departures(slots, input.Trips);
        var horizonEnd = slots[^1].EndsAtUtc;
        var futureValue = socKnown ? (input.FutureValuePerKwh ?? Median(slots.Select(slot => slot.PricePerKwh)) * FutureValueShare) / efficiency : 0;
        var baseTarget = baseEnergy;
        var terminalTarget = TerminalTripTarget(input.Trips, horizonEnd, capacity, car, efficiency);

        // Energy states: 1 kWh steps up to the car's limit plus the exact current, reserve and base values.
        var states = Enumerable.Range(0, (int)Math.Floor(limit) + 1).Select(kwh => (double)kwh)
            .Concat([start, reserve, baseEnergy, limit]).Where(e => e >= 0 && e <= Math.Max(limit, start)).Distinct().Order().ToArray();
        var powers = Enumerable.Range(6, 11).Select(amps => Math.Min(amps * WattsPerAmp, car.ChargePowerWatts) / 1000d)
            .Where(power => power * 1000 >= MinimumChargePowerWatts - 1).Distinct().ToArray();

        double Terminal(double energy) =>
            -futureValue * Math.Min(energy, limit)
            + BaseShortfallPenaltyPerKwh * Math.Max(0, baseTarget - energy)
            + TripShortfallPenaltyPerKwh * Math.Max(0, terminalTarget - energy);

        // Cost of w kWh at the wallbox in slot i: the sources are used in order of their price (the home battery's PV
        // can be dearer than grid power when it saves an expensive evening), with the PV share the plan relies on.
        (double Cost, double GridKwh, double PvKwh) SlotCost(int i, double wallboxKwh, double pvFactor)
        {
            var slot = slots[i];
            var gridPrice = slot.PricePerKwh + (slot.IsPriceEstimated ? EstimatedPriceHurdlePerKwh : 0);
            var sources = new[]
            {
                (Kwh: pvFactor * slot.SurplusKwh, Price: input.FeedInPricePerKwh, IsPv: true),
                (Kwh: pvFactor * slot.BatteryPvKwh, Price: slot.BatteryPvValuePerKwh, IsPv: true),
                (Kwh: double.MaxValue, Price: gridPrice, IsPv: false)
            };
            double remaining = wallboxKwh, cost = 0, grid = 0, pv = 0;
            foreach (var source in sources.OrderBy(source => source.Price))
            {
                var take = Math.Min(remaining, Math.Max(0, source.Kwh));
                cost += take * source.Price;
                if (source.IsPv) pv += take; else grid += take;
                remaining -= take;
                if (remaining <= 0) break;
            }

            return (cost, grid, pv);
        }

        // Hours at home below the base state of charge; away (no hours) nothing can be done about it.
        double BelowBase(int i, double energyAfter) => BelowBasePenaltyPerKwhHour * Math.Max(0, baseEnergy - energyAfter) * hours[i];

        (double Power, double Cost) BestAction(int i, double energy, double[] next)
        {
            var best = (Power: 0d, Cost: Interpolate(next, states, energy) + BelowBase(i, energy));
            if (hours[i] <= 0) return best;
            var candidates = powers.ToList();
            // Exactly filling up to the limit may need a power between the discrete steps.
            var fill = (limit - energy) / (hours[i] * efficiency);
            if (fill * 1000 >= MinimumChargePowerWatts && fill <= car.ChargePowerWatts / 1000d) candidates.Add(fill);
            foreach (var power in candidates)
            {
                var after = energy + power * hours[i] * efficiency;
                if (after > limit + 1e-6) continue;
                var cost = SlotCost(i, power * hours[i], input.PvConfidence).Cost + Interpolate(next, states, after) + BelowBase(i, after) + power * 1e-6;
                if (cost < best.Cost - 1e-9) best = (power, cost);
            }

            return best;
        }

        var values = new double[slots.Length + 1][];
        values[^1] = states.Select(Terminal).ToArray();
        for (var i = slots.Length - 1; i >= 0; i--)
        {
            values[i] = new double[states.Length];
            for (var s = 0; s < states.Length; s++)
            {
                var (energy, eventPenalty) = ApplyDeparture(departures, i, states[s], reserve);
                values[i][s] = eventPenalty + BestAction(i, energy, values[i + 1]).Cost;
            }
        }

        // Forward pass from the actual state of charge.
        var planned = new List<(int Index, double Power, double EnergyAfter)>();
        var current = start;
        var trace = new double[slots.Length + 1];
        trace[0] = start;
        for (var i = 0; i < slots.Length; i++)
        {
            if (departures.TryGetValue(i, out var leaving) && current < reserve + leaving.EnergyKwh - 0.1)
                warnings.Add($"Bis zur Abfahrt nach {leaving.Destination} fehlen {reserve + leaving.EnergyKwh - current:0.0} kWh, die in der verfügbaren Zeit nicht ladbar sind.");
            var (energy, _) = ApplyDeparture(departures, i, current, reserve);
            var power = BestAction(i, energy, values[i + 1]).Power;
            current = energy + power * hours[i] * efficiency;
            trace[i + 1] = current;
            if (power > 0) planned.Add((i, power, current));
        }

        if (terminalTarget - current > 0.1)
            warnings.Add($"Für die nächste Fahrt nach dem Planungszeitraum fehlen voraussichtlich {terminalTarget - current:0.0} kWh.");
        if (baseTarget - current > 0.1 && departures.Count == 0 && terminalTarget <= 0)
            warnings.Add("Der Grund-Ladestand ist im Planungszeitraum nicht ganz erreichbar.");

        // Why a quarter hour charges: for a departure if the car leaves with little more than it needs (one current step
        // of the wallbox adds up to about 1 kWh), else by its main source.
        const double NeedTolerance = 1.5;
        EvChargeReason Reason(int index, double energyAfter, double grid, double pv)
        {
            var nextDeparture = departures.Keys.Where(key => key > index).DefaultIfEmpty(-1).Min();
            if (nextDeparture >= 0 && trace[nextDeparture] <= reserve + departures[nextDeparture].EnergyKwh + NeedTolerance) return EvChargeReason.Trip;
            if (nextDeparture < 0 && terminalTarget > 0 && current <= terminalTarget + NeedTolerance) return EvChargeReason.Trip;
            if (pv >= grid) return EvChargeReason.PvSurplus;
            return energyAfter <= baseEnergy + 0.5 ? EvChargeReason.BaseSoc : EvChargeReason.CheapPrice;
        }

        var plannedSlots = planned.Select(entry =>
        {
            var slot = slots[entry.Index];
            var (_, grid, pv) = SlotCost(entry.Index, entry.Power * hours[entry.Index], 1.0);
            return new EvPlannedChargeSlot(slot.StartsAtUtc, slot.EndsAtUtc, entry.Power * 1000, grid, pv, slot.PricePerKwh, slot.IsPriceEstimated,
                Reason(entry.Index, entry.EnergyAfter, grid, pv));
        }).ToArray();

        var scenarios = ScenarioFactors.Select(scenario =>
        {
            var costs = planned.Select(entry => (Entry: entry, Cost: SlotCost(entry.Index, entry.Power * hours[entry.Index], scenario.Factor))).ToArray();
            return new EvChargeScenario(scenario.Name, scenario.Factor, costs.Sum(pair => pair.Cost.GridKwh),
                costs.Sum(pair => pair.Cost.GridKwh * slots[pair.Entry.Index].PricePerKwh));
        }).ToArray();
        return new EvChargePlan(input.NowUtc, plannedSlots, plannedSlots.Sum(slot => slot.GridKwh), plannedSlots.Sum(slot => slot.PvKwh),
            plannedSlots.Sum(slot => slot.GridKwh * slot.PricePerKwh), warnings) { Scenarios = scenarios };
    }

    /// <summary>At a departure the trip energy leaves the battery; coming back below the minimum costs a penalty.</summary>
    private static (double Energy, double Penalty) ApplyDeparture(IReadOnlyDictionary<int, (double EnergyKwh, string Destination)> departures,
        int index, double energy, double reserve)
    {
        if (!departures.TryGetValue(index, out var trip)) return (energy, 0);
        var after = energy - trip.EnergyKwh;
        return (Math.Max(0, after), Math.Max(0, reserve - after) * TripShortfallPenaltyPerKwh);
    }

    // Departures inside the horizon, keyed by the first slot that starts at or after the departure.
    private static Dictionary<int, (double EnergyKwh, string Destination)> Departures(EvChargeSlotInput[] slots, IReadOnlyList<EvTripRequirement> trips)
    {
        var result = new Dictionary<int, (double EnergyKwh, string Destination)>();
        foreach (var trip in trips)
        {
            var index = Array.FindIndex(slots, slot => slot.StartsAtUtc >= trip.Trip.DepartureAtUtc);
            if (index < 0) continue;
            result[index] = result.TryGetValue(index, out var existing)
                ? (existing.EnergyKwh + trip.EnergyKwh, existing.Destination)
                : (trip.EnergyKwh, trip.Trip.Destination);
        }

        return result;
    }

    /// <summary>
    /// Departures after the horizon: what must be in the battery at its end. Prices after the horizon are not published
    /// yet, so half of what full power could charge until that departure may wait for them.
    /// </summary>
    private static double TerminalTripTarget(IReadOnlyList<EvTripRequirement> trips, DateTimeOffset horizonEnd, double capacity,
        EvCarParameters car, double efficiency)
    {
        var next = trips.Where(trip => trip.Trip.DepartureAtUtc >= horizonEnd).MinBy(trip => trip.Trip.DepartureAtUtc);
        if (next is null) return 0;
        var need = Math.Min(next.RequiredSocPercent, 100) / 100d * capacity;
        var deferable = (next.Trip.DepartureAtUtc - horizonEnd).TotalHours * car.ChargePowerWatts / 1000d * efficiency * 0.5;
        return Math.Max(0, need - deferable);
    }

    // Hours of the slot the car can charge: not before now and not while it is away on a trip.
    private static double AvailableHours(EvChargeSlotInput slot, DateTimeOffset now, IReadOnlyList<EvTripRequirement> trips)
    {
        var start = slot.StartsAtUtc < now ? now : slot.StartsAtUtc;
        if (start >= slot.EndsAtUtc) return 0;
        if (trips.Any(trip => trip.Trip.DepartureAtUtc < slot.EndsAtUtc && trip.ReturnAtUtc > start)) return 0;
        return (slot.EndsAtUtc - start).TotalHours;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static double Interpolate(double[] values, double[] states, double energy)
    {
        var index = Array.BinarySearch(states, energy);
        if (index >= 0) return values[index];
        index = ~index;
        if (index <= 0) return values[0];
        if (index >= states.Length) return values[^1];
        var weight = (energy - states[index - 1]) / (states[index] - states[index - 1]);
        return values[index - 1] + weight * (values[index] - values[index - 1]);
    }
}
