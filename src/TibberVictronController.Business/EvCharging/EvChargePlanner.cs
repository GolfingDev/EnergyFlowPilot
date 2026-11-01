using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>One quarter hour offered to the car: grid price and PV that would otherwise be fed in.</summary>
public sealed record EvChargeSlotInput(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, double PricePerKwh, bool IsPriceEstimated, double SurplusKwh);

public sealed record EvChargePlanInput(
    DateTimeOffset NowUtc,
    double? CurrentSocPercent,
    double? CarTargetSocPercent,
    EvCarParameters Car,
    double BaseSocPercent,
    double FeedInPricePerKwh,
    IReadOnlyList<EvChargeSlotInput> Slots,
    IReadOnlyList<EvTripRequirement> Trips);

public enum EvChargeReason
{
    Trip,
    PvSurplus,
    BaseSoc
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

public sealed record EvChargePlan(
    DateTimeOffset CalculatedAtUtc,
    IReadOnlyList<EvPlannedChargeSlot> Slots,
    double GridKwh,
    double PvKwh,
    double GridCost,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Quarter-hour charging plan for the car (rules 3 and 6 of ev-charging.md). For each departure, in order, the
/// missing energy goes into the cheapest quarter hours before it: PV surplus counts at the feed-in price, grid at
/// its price. Afterwards remaining PV surplus is always used (up to the car's own charge limit), and the cheapest
/// quarter hours fill up to the base state of charge. The car is away between departure and return.
/// The wallbox cannot charge below six amps on three phases, so a planned quarter hour never goes below that.
/// </summary>
public static class EvChargePlanner
{
    // Halo: 6 A x 230 V x 3 phases, no single-phase switching.
    public const double MinimumChargePowerWatts = 4140;

    public static EvChargePlan Plan(EvChargePlanInput input)
    {
        var car = input.Car;
        var capacity = car.UsableCapacityKwh;
        var efficiency = EvTripSocPlanner.ChargeEfficiency;
        var warnings = new List<string>();
        var slots = input.Slots.Where(slot => slot.EndsAtUtc > input.NowUtc).OrderBy(slot => slot.StartsAtUtc).ToList();
        var state = slots.Select(slot => new SlotState(slot, AvailableHours(slot, input.NowUtc, input.Trips))).ToList();
        var current = (input.CurrentSocPercent ?? car.MinimumSocPercent) / 100d * capacity;
        if (input.CurrentSocPercent is null) warnings.Add("Ladestand des Autos unbekannt; geplant wird ab dem Mindest-Ladestand.");
        var horizonEnd = slots.Count > 0 ? slots[^1].EndsAtUtc : input.NowUtc;
        if (slots.Count == 0)
            warnings.Add("Keine Preise und kein Akkuplan vorhanden; ein Ladeplan ist erst damit möglich.");

        // 1. Departures, earliest first: each must find its deficit before it leaves.
        var tripEnergyBefore = 0d;
        foreach (var trip in input.Trips.OrderBy(trip => trip.Trip.DepartureAtUtc))
        {
            var departure = trip.Trip.DepartureAtUtc;
            var need = Math.Min(trip.RequiredSocPercent, 100) / 100d * capacity;
            var plannedBefore = state.Where(slot => slot.Input.EndsAtUtc <= departure).Sum(slot => slot.BatteryKwh(efficiency));
            var deficit = need - current + tripEnergyBefore - plannedBefore;
            if (departure > horizonEnd)
            {
                // Prices after the horizon are not published yet; what fits in that time can wait for them.
                var deferable = (departure - horizonEnd).TotalHours * car.ChargePowerWatts / 1000d * efficiency * 0.5;
                deficit -= deferable;
            }

            if (deficit > 0.05)
            {
                var missing = Allocate(state.Where(slot => slot.Input.EndsAtUtc <= departure), deficit, car, efficiency, input.FeedInPricePerKwh, EvChargeReason.Trip);
                if (missing > 0.05)
                    warnings.Add($"Bis zur Abfahrt nach {trip.Trip.Destination} fehlen {missing:0.0} kWh, die in der verfügbaren Zeit nicht ladbar sind.");
            }

            tripEnergyBefore += trip.EnergyKwh;
        }

        // 2. PV surplus is always used, up to the charge limit set in the car.
        var limit = Math.Min(input.CarTargetSocPercent ?? 100, 100) / 100d * capacity;
        var room = limit - current + tripEnergyBefore - state.Sum(slot => slot.BatteryKwh(efficiency));
        foreach (var slot in state.Where(slot => slot.Input.SurplusKwh > 0).OrderBy(slot => slot.Input.StartsAtUtc))
        {
            if (room <= 0.05) break;
            var before = slot.BatteryKwh(efficiency);
            slot.AddPv(car, efficiency, room, onlyIfAtLeastMinimum: true);
            room -= slot.BatteryKwh(efficiency) - before;
        }

        // 3. Base state of charge by the end of the horizon, from the cheapest remaining quarter hours.
        var baseNeed = Math.Min(input.BaseSocPercent, Math.Min(input.CarTargetSocPercent ?? 100, 100)) / 100d * capacity;
        var baseDeficit = baseNeed - current + tripEnergyBefore - state.Sum(slot => slot.BatteryKwh(efficiency));
        if (baseDeficit > 0.05 && slots.Count > 0 &&
            Allocate(state, baseDeficit, car, efficiency, input.FeedInPricePerKwh, EvChargeReason.BaseSoc) > 0.05)
            warnings.Add("Der Grund-Ladestand ist im Planungszeitraum nicht ganz erreichbar.");

        var planned = state.Where(slot => slot.GridKwh + slot.PvKwh > 0).Select(slot => new EvPlannedChargeSlot(
            slot.Input.StartsAtUtc, slot.Input.EndsAtUtc, (slot.GridKwh + slot.PvKwh) / slot.Hours * 1000d, slot.GridKwh, slot.PvKwh,
            slot.Input.PricePerKwh, slot.Input.IsPriceEstimated, slot.Reason)).ToArray();
        return new EvChargePlan(input.NowUtc, planned, planned.Sum(slot => slot.GridKwh), planned.Sum(slot => slot.PvKwh),
            planned.Sum(slot => slot.GridKwh * slot.PricePerKwh), warnings);
    }

    /// <summary>Fills the deficit (battery-side kWh) from the cheapest offers; returns what could not be placed.</summary>
    private static double Allocate(IEnumerable<SlotState> candidates, double deficit, EvCarParameters car, double efficiency,
        double feedInPrice, EvChargeReason reason)
    {
        var offers = candidates.Where(slot => slot.Hours > 0)
            .SelectMany(slot => new[] { (Slot: slot, Pv: true, Cost: feedInPrice), (Slot: slot, Pv: false, Cost: slot.Input.PricePerKwh) })
            .Where(offer => offer.Pv ? offer.Slot.Input.SurplusKwh > offer.Slot.PvKwh : true)
            .OrderBy(offer => offer.Cost)
            .ThenBy(offer => offer.Slot.Input.StartsAtUtc)
            .ToList();
        foreach (var offer in offers)
        {
            if (deficit <= 0.05) break;
            var before = offer.Slot.BatteryKwh(efficiency);
            if (offer.Pv) offer.Slot.AddPv(car, efficiency, deficit, onlyIfAtLeastMinimum: false);
            else offer.Slot.AddGrid(car, efficiency, deficit);
            var added = offer.Slot.BatteryKwh(efficiency) - before;
            if (added > 0 && offer.Slot.Reason != EvChargeReason.Trip) offer.Slot.Reason = reason;
            deficit -= added;
        }

        return Math.Max(0, deficit);
    }

    // Hours of the slot the car can charge: not before now and not while it is away on a trip.
    private static double AvailableHours(EvChargeSlotInput slot, DateTimeOffset now, IReadOnlyList<EvTripRequirement> trips)
    {
        var start = slot.StartsAtUtc < now ? now : slot.StartsAtUtc;
        if (start >= slot.EndsAtUtc) return 0;
        if (trips.Any(trip => trip.Trip.DepartureAtUtc < slot.EndsAtUtc && trip.ReturnAtUtc > start)) return 0;
        return (slot.EndsAtUtc - start).TotalHours;
    }

    private sealed class SlotState(EvChargeSlotInput input, double hours)
    {
        public EvChargeSlotInput Input { get; } = input;
        public double Hours { get; } = hours;
        public double GridKwh { get; private set; }
        public double PvKwh { get; private set; }
        public EvChargeReason Reason { get; set; } = EvChargeReason.PvSurplus;

        public double BatteryKwh(double efficiency) => (GridKwh + PvKwh) * efficiency;

        private double FreeKwh(EvCarParameters car) => Math.Max(0, car.ChargePowerWatts / 1000d * Hours - GridKwh - PvKwh);
        private double MinimumKwh => MinimumChargePowerWatts / 1000d * Hours;

        public void AddPv(EvCarParameters car, double efficiency, double batteryKwhWanted, bool onlyIfAtLeastMinimum)
        {
            var pv = Math.Min(Math.Min(Input.SurplusKwh - PvKwh, FreeKwh(car)), batteryKwhWanted / efficiency);
            // Surplus below the wallbox minimum would pull the rest from the grid; as a pure PV slot it is skipped.
            if (pv <= 0 || onlyIfAtLeastMinimum && GridKwh + PvKwh + pv < MinimumKwh) return;
            PvKwh += pv;
            RaiseToMinimum();
        }

        public void AddGrid(EvCarParameters car, double efficiency, double batteryKwhWanted)
        {
            var grid = Math.Min(FreeKwh(car), batteryKwhWanted / efficiency);
            if (grid <= 0) return;
            GridKwh += grid;
            RaiseToMinimum();
        }

        // The wallbox cannot go below its minimum current; the missing part comes from the grid.
        private void RaiseToMinimum()
        {
            var missing = MinimumKwh - GridKwh - PvKwh;
            if (missing > 0) GridKwh += missing;
        }
    }
}
