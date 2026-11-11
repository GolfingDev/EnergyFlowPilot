using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>
/// One quarter hour offered to the car: grid price, PV that would otherwise be fed in, and PV the home battery plan
/// would store (the car takes it only for a coming trip).
/// </summary>
public sealed record EvChargeSlotInput(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, double PricePerKwh, bool IsPriceEstimated, double SurplusKwh)
{
    public double BatteryPvKwh { get; init; }
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
    /// Long-term cheap grid price (operator 2026-11-06: a percentile of the last 30 days): beyond its need the car fills
    /// up from the grid only below it. Null: no filling from the grid, only with PV surplus.
    /// </summary>
    public double? FutureValuePerKwh { get; init; }

    /// <summary>Share of the PV forecast the plan relies on; trips must work even when the sun disappoints.</summary>
    public double PvConfidence { get; init; } = 0.7;

    /// <summary>Below the base state of charge: cheapest hours within a day (default) or at once (driver's answer).</summary>
    public EvBaseChargeMode BaseMode { get; init; } = EvBaseChargeMode.PriceDriven;

    /// <summary>Charge at once up to this state of charge (set by hand for the running session), then follow the plan.</summary>
    public double? ImmediateTargetSocPercent { get; init; }
}

public enum EvChargeReason
{
    Trip,
    PvSurplus,
    BaseSoc,
    CheapPrice,
    ImmediateTarget
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

/// <summary>
/// A charging goal and when the plan reaches it (operator 2026-11-11: the page did not say when the base state of charge
/// would be charged, and claimed nothing was needed before a trip). ReachedAtUtc null: not inside the planned prices.
/// </summary>
public sealed record EvChargeGoal(EvChargeReason Kind, double TargetSocPercent, DateTimeOffset? DueAtUtc, string? Destination, DateTimeOffset? ReachedAtUtc)
{
    /// <summary>Left for prices not known yet: the plan charges it once they are published.</summary>
    public bool WaitsForPrices { get; init; }
}

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

    public IReadOnlyList<EvChargeGoal> Goals { get; init; } = [];
}

/// <summary>
/// Cost-optimal charging plan for the car: dynamic programming over the car's battery energy per quarter hour.
/// Each kWh at the wallbox comes from the cheapest source of its slot: PV that would be fed in (feed-in price), then
/// the grid. PV the home batteries would store goes to them first; the car gets it, ahead of them, only while it
/// needs energy for a coming trip (operator 2026-11-06). Departures are hard requirements (the car must come back
/// with at least the minimum state of charge); the car is away between departure and return. Beyond trips, base
/// state of charge and a target set by hand the car charges only what is cheap: grid power below the long-term cheap
/// price, or PV surplus, up to the car's own limit; a target set by hand caps the grid share. The base state of
/// charge is reached within a day, or at once when the driver chose so. The wallbox charges with 0 or 6 to 16 A on
/// three phases.
/// </summary>
public static class EvChargePlanner
{
    // Halo: 6 A x 230 V x 3 phases, no single-phase switching.
    public const double MinimumChargePowerWatts = 4140;
    public const double WattsPerAmp = 690;
    // Missing energy at a departure must never be "cheaper" than any charging; below the base state of charge is
    // strongly, but not absolutely, avoided.
    private const double TripShortfallPenaltyPerKwh = 10;
    private const double BaseShortfallPenaltyPerKwh = 1;
    // "Charge now" (operator 2026-11-03): every hour at home below the base state of charge counts against the plan, far
    // above any price difference, so the car charges at full power until the base is reached.
    private const double BelowBasePenaltyPerKwhHour = 5;
    // "By price": the base state of charge should be reached within a day, not necessarily before the published prices end.
    private static readonly TimeSpan BaseSocDeadline = TimeSpan.FromHours(24);
    private const double EstimatedPriceHurdlePerKwh = 0.02;
    // A forecast gets less reliable the further ahead it is, so waiting for it must pay off by more.
    private const double EstimatedPriceHurdlePerDayPerKwh = 0.01;
    // Bonus per kWh of PV surplus in the optimiser, so it is always charged (up to the car's limit).
    private const double PvFillMarginPerKwh = 0.01;
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
        // Energy beyond the need is worth the long-term cheap price (per kWh in the battery), so grid power fills up only
        // below it; without a price history nothing. The cheapest hours of an expensive phase are not cheap (operator
        // 2026-11-06: 0.42 EUR/kWh overnight). PV surplus is taken by its own (negative) price, see SlotCost.
        var futureValue = socKnown ? (input.FutureValuePerKwh ?? 0) / efficiency : 0;
        var immediate = input.BaseMode == EvBaseChargeMode.Immediate;
        // By price: what full power could charge between the end of the known prices and the base deadline may wait for
        // those prices (half of it, for safety), so a cheap night tomorrow can still be used.
        var baseDeferable = immediate ? 0 : Math.Max(0, (input.NowUtc + BaseSocDeadline - horizonEnd).TotalHours) * car.ChargePowerWatts / 1000d * efficiency * 0.5;
        var baseTarget = Math.Max(0, baseEnergy - baseDeferable);
        // Charged at full power right away: the base state of charge when the driver chose "now", and a target set by hand.
        var manualEnergy = input.ImmediateTargetSocPercent is { } manual ? Math.Clamp(manual / 100d * capacity, 0, limit) : 0;
        var immediateEnergy = Math.Max(immediate ? baseEnergy : 0, manualEnergy);
        var terminalTarget = TerminalTripTarget(input.Trips, horizonEnd, capacity, car, efficiency);
        // A target set by hand caps filling from the grid: above it only PV surplus is worth charging (operator 2026-11-06).
        var gridFillCap = manualEnergy > 0 ? manualEnergy : limit;
        // PV the home batteries would store goes to them first; the car gets it, ahead of them, only for the energy it
        // still lacks for the next trip and only before that departure (operator 2026-11-06).
        var batteryPvForTrip = BatteryPvForTrip(slots, departures, Math.Max(0, NextTripNeed(departures, terminalTarget, reserve) - start) / efficiency);

        // Energy states: 1 kWh steps up to the car's limit plus the exact current, reserve and base values.
        var states = Enumerable.Range(0, (int)Math.Floor(limit) + 1).Select(kwh => (double)kwh)
            .Concat([start, reserve, baseEnergy, manualEnergy, limit]).Where(e => e >= 0 && e <= Math.Max(limit, start)).Distinct().Order().ToArray();
        var powers = Enumerable.Range(6, 11).Select(amps => Math.Min(amps * WattsPerAmp, car.ChargePowerWatts) / 1000d)
            .Where(power => power * 1000 >= MinimumChargePowerWatts - 1).Distinct().ToArray();

        double Terminal(double energy) =>
            -futureValue * Math.Min(energy, gridFillCap)
            + BaseShortfallPenaltyPerKwh * Math.Max(0, baseTarget - energy)
            + TripShortfallPenaltyPerKwh * Math.Max(0, terminalTarget - energy);

        // Cost of w kWh at the wallbox in slot i: the sources are used in order of their price, with the PV share the
        // plan relies on. PV surplus is always taken, up to the car's limit (operator 2026-11-06: PV fills the batteries
        // until all are full): a small bonus instead of its feed-in price, so it never makes grid energy look worth more.
        (double Cost, double GridKwh, double PvKwh) SlotCost(int i, double wallboxKwh, double pvFactor)
        {
            var slot = slots[i];
            var gridPrice = slot.PricePerKwh + (slot.IsPriceEstimated
                ? EstimatedPriceHurdlePerKwh + EstimatedPriceHurdlePerDayPerKwh * Math.Max(0, (slot.StartsAtUtc - input.NowUtc).TotalDays)
                : 0);
            var sources = new[]
            {
                // Unknown state of charge: no filling up, so PV surplus keeps its feed-in price and serves only trips.
                (Kwh: pvFactor * slot.SurplusKwh, Price: socKnown ? -PvFillMarginPerKwh : input.FeedInPricePerKwh, IsPv: true),
                (Kwh: pvFactor * batteryPvForTrip[i], Price: input.FeedInPricePerKwh, IsPv: true),
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
        double BelowBase(int i, double energyAfter) => BelowBasePenaltyPerKwhHour * Math.Max(0, immediateEnergy - energyAfter) * hours[i];

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
            if (manualEnergy > 0 && trace[index] < manualEnergy - 0.1)
                return EvChargeReason.ImmediateTarget;
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
            plannedSlots.Sum(slot => slot.GridKwh * slot.PricePerKwh), warnings)
        {
            Scenarios = scenarios,
            Goals = Goals(new GoalContext(input, slots, trace, start, baseEnergy, baseDeferable > 0))
        };
    }

    private sealed record GoalContext(EvChargePlanInput Input, EvChargeSlotInput[] Slots, double[] Trace, double Start, double BaseEnergy, bool BaseMayWait);

    // The base state of charge (when below it) and the next trip, each with the time the planned course reaches it.
    private static IReadOnlyList<EvChargeGoal> Goals(GoalContext context)
    {
        var (input, slots, trace) = (context.Input, context.Slots, context.Trace);
        var capacity = input.Car.UsableCapacityKwh;
        DateTimeOffset? ReachedAt(double target, int endIndex)
        {
            if (context.Start >= target - 0.05) return input.NowUtc;
            for (var i = 0; i < endIndex; i++)
                if (trace[i + 1] >= target - 0.05) return slots[i].EndsAtUtc;
            return null;
        }

        var goals = new List<EvChargeGoal>();
        if (input.CurrentSocPercent is not null && context.BaseEnergy > context.Start + 0.05)
        {
            var reached = ReachedAt(context.BaseEnergy, slots.Length);
            goals.Add(new EvChargeGoal(EvChargeReason.BaseSoc, input.BaseSocPercent, null, null, reached) { WaitsForPrices = reached is null && context.BaseMayWait });
        }

        var trip = input.Trips.Where(trip => trip.Trip.DepartureAtUtc > input.NowUtc).MinBy(trip => trip.Trip.DepartureAtUtc);
        if (trip is not null)
        {
            var departure = Array.FindIndex(slots, slot => slot.StartsAtUtc >= trip.Trip.DepartureAtUtc);
            var reached = ReachedAt(Math.Min(trip.RequiredSocPercent, 100) / 100d * capacity, departure >= 0 ? departure : slots.Length);
            goals.Add(new EvChargeGoal(EvChargeReason.Trip, trip.RequiredSocPercent, trip.Trip.DepartureAtUtc, trip.Trip.Destination, reached)
            {
                WaitsForPrices = reached is null && departure < 0
            });
        }

        return goals;
    }

    // Energy the car needs for its next trip: reserve plus trip energy at the first departure in the horizon, else
    // what must be in the battery at the horizon end for a later one; 0 without a trip.
    private static double NextTripNeed(IReadOnlyDictionary<int, (double EnergyKwh, string Destination)> departures, double terminalTarget,
        double reserve) =>
        departures.Count > 0 ? reserve + departures[departures.Keys.Min()].EnergyKwh : terminalTarget;

    // Home battery PV the car may take per slot: the earliest slots before the next departure (all slots for a trip
    // after the horizon) until the missing wallbox energy is covered; nothing when the car already has enough.
    private static double[] BatteryPvForTrip(EvChargeSlotInput[] slots, IReadOnlyDictionary<int, (double EnergyKwh, string Destination)> departures,
        double missingWallboxKwh)
    {
        var result = new double[slots.Length];
        var end = departures.Count > 0 ? departures.Keys.Min() : slots.Length;
        var budget = missingWallboxKwh;
        for (var i = 0; i < end && budget > 0; i++)
        {
            result[i] = Math.Min(Math.Max(0, slots[i].BatteryPvKwh), budget);
            budget -= result[i];
        }

        return result;
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
