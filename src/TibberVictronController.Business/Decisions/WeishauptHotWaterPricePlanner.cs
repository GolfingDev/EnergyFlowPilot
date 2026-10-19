using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Plans a continuous charging window against the next configured demand, with explicit loss assumptions.</summary>
public static class WeishauptHotWaterPricePlanner
{
    private static readonly TimeZoneInfo LocalZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static WeishauptHotWaterPricePlan Plan(WeishauptHotWaterSettings settings, DateTimeOffset nowUtc,
        IReadOnlyList<TibberPriceForecastSlot> prices, IReadOnlyList<WeishauptHotWaterEpisodeAnalysis> history)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, LocalZone);
        var demand = local.Date.AddHours(settings.DemandHourLocal);
        if (demand <= local.DateTime) demand = demand.AddDays(1);
        // In the autumn overlap choose the later instant; in the spring gap use the first valid time.
        while (LocalZone.IsInvalidTime(demand)) demand = demand.AddMinutes(1);
        var need = LocalZone.IsAmbiguousTime(demand)
            ? new DateTimeOffset(demand, LocalZone.GetAmbiguousTimeOffsets(demand).Min()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(demand, LocalZone));

        var usable = history.Where(h => !h.HasDataGaps && !h.Episode.Interrupted && h.Episode.WriteConfirmed).ToArray();
        var holds = usable.Where(h => h.TargetReachedAtUtc.HasValue && h.ReserveLostAtUtc > h.TargetReachedAtUtc &&
                h.ReserveLostAtUtc <= h.Episode.Plan.NeedByUtc)
            .Select(h => (decimal)(h.ReserveLostAtUtc!.Value - h.TargetReachedAtUtc!.Value).TotalHours).Order().ToArray();
        var hold = holds.Length < 3 ? settings.InitialHoldHours
            : Math.Min(settings.InitialHoldHours, Math.Clamp(holds[(holds.Length - 1) / 4], 1m, 12m));
        var charges = usable.Where(h => h.TargetReachedAtUtc.HasValue)
            .Select(h => (int)Math.Ceiling((h.TargetReachedAtUtc!.Value - h.Episode.StartedAtUtc).TotalMinutes)).Order().ToArray();
        var charge = charges.Length < 3 ? settings.InitialChargeMinutes : Math.Clamp(charges[charges.Length / 2] + 15, 15, 180);
        var empty = new WeishauptHotWaterPricePlan(need, null, null, null, null, null, hold, charge,
            "Keine vollstaendigen veroeffentlichten Preise bis zur Bedarfszeit.");

        var ordered = prices.Where(p => !p.IsEstimated && p.Currency == "EUR" && p.TimeSlot.Duration > TimeSpan.Zero)
            .OrderBy(p => p.TimeSlot.StartsAtUtc).ToArray();
        var reference = Average(ordered, need, need.AddMinutes(charge));
        // Observed holding time starts after charging, not at the beginning of the charge.
        var earliest = need.AddHours(-(double)hold).AddMinutes(-charge);
        if (earliest < nowUtc) earliest = nowUtc;
        // Missing intermediate prices could hide a cheaper window: do not guess it away.
        if (reference is null || Average(ordered, earliest, need.AddMinutes(charge)) is null) return empty;

        var candidates = ordered.Select(p => p.TimeSlot.StartsAtUtc < earliest ? earliest : p.TimeSlot.StartsAtUtc)
            .Append(need.AddMinutes(-charge)).Distinct()
            .Where(start => start >= earliest && start.AddMinutes(charge) <= need)
            .Select(start => new { Start = start, Price = Average(ordered, start, start.AddMinutes(charge)) })
            .Where(c => c.Price.HasValue).OrderBy(c => c.Price).ThenByDescending(c => c.Start).ToArray();
        if (candidates.Length == 0) return empty with { Reason = "Kein ausreichend langes Ladefenster vor dem Bedarf." };
        var best = candidates[0];
        var effective = EffectivePrice(best.Price!.Value, settings.ExtraEnergyPercent);
        var advantage = reference.Value - effective;
        return new WeishauptHotWaterPricePlan(need, best.Start, best.Start.AddMinutes(charge), best.Price, reference,
            advantage, hold, charge, advantage >= settings.MinimumPriceAdvantagePerKwh
                ? "Guenstigstes zusammenhaengendes Ladefenster vor der Bedarfszeit."
                : "Preisabstand deckt Mehrverbrauchsannahme und Mindestvorteil nicht.");
    }

    // Do not count additional losses as an extra reward when electricity prices are negative.
    public static decimal EffectivePrice(decimal price, decimal extraEnergyPercent) =>
        price + Math.Max(0m, price) * extraEnergyPercent / 100m;

    private static decimal? Average(IReadOnlyList<TibberPriceForecastSlot> slots, DateTimeOffset start, DateTimeOffset end)
    {
        var cursor = start;
        decimal weighted = 0;
        foreach (var slot in slots.Where(p => p.TimeSlot.EndsAtUtc > start && p.TimeSlot.StartsAtUtc < end))
        {
            // Gaps, overlaps and duplicate slots are ambiguous inputs, not a valid cost forecast.
            if (slot.TimeSlot.StartsAtUtc > cursor || (cursor > start && slot.TimeSlot.StartsAtUtc < cursor)) return null;
            var until = slot.TimeSlot.EndsAtUtc < end ? slot.TimeSlot.EndsAtUtc : end;
            weighted += slot.TotalPricePerKwh * (decimal)(until - cursor).TotalMinutes;
            cursor = until;
            if (cursor == end) return weighted / (decimal)(end - start).TotalMinutes;
        }
        return null;
    }
}
