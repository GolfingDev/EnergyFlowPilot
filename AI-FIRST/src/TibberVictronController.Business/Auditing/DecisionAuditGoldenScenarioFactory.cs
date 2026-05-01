using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Creates deterministic 24-hour audit scenarios for user-readable Decision Engine reviews.
/// </summary>
public static class DecisionAuditGoldenScenarioFactory
{
    private static readonly DateTimeOffset DefaultScenarioStartsAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates the default 24-hour multi-family household scenario with 96 continuous slots.
    /// </summary>
    public static DecisionAuditScenario Create(
        decimal initialStateOfChargePercent = 37m,
        bool includeNegativePrices = true)
    {
        return Create(DefaultScenarioStartsAtUtc, initialStateOfChargePercent, includeNegativePrices);
    }

    /// <summary>
    /// Creates a 24-hour multi-family household scenario starting at the provided UTC timestamp.
    /// </summary>
    public static DecisionAuditScenario Create(
        DateTimeOffset startsAtUtc,
        decimal initialStateOfChargePercent,
        bool includeNegativePrices)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Audit-Start muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        var timeSlots = CreateTimeSlots(startsAtUtc, slotCount: 96);
        var prices = timeSlots
            .Select(timeSlot => new TibberPriceForecastSlot(timeSlot, DeterminePrice(timeSlot.StartsAtUtc, includeNegativePrices), "EUR"))
            .ToArray();
        var pvYield = timeSlots
            .Select(timeSlot => new PvYieldForecastSlot(timeSlot, DeterminePvYieldKwh(timeSlot.StartsAtUtc)))
            .ToArray();
        var consumption = timeSlots
            .Select(timeSlot => new ConsumptionForecastSlot(timeSlot, DetermineConsumptionKwh(timeSlot.StartsAtUtc)))
            .ToArray();

        return new DecisionAuditScenario(
            Name: "Golden 24h Mehrfamilienhaus",
            InitialBatteryState: new BatteryState(initialStateOfChargePercent, startsAtUtc),
            BatteryConfiguration: new BatteryConfiguration(
                totalCapacityKwh: 12m,
                minimumStateOfChargePercent: 10m,
                maximumChargePowerWatts: 3000,
                maximumDischargePowerWatts: 3000,
                roundTripEfficiencyPercent: 90m),
            PriceForecast: prices,
            PvForecast: pvYield,
            ConsumptionForecast: consumption,
            FeedInCompensationPricePerKwh: 0.08m);
    }

    private static IReadOnlyList<ForecastTimeSlot> CreateTimeSlots(DateTimeOffset startsAtUtc, int slotCount)
    {
        return Enumerable.Range(0, slotCount)
            .Select(index => new ForecastTimeSlot(
                startsAtUtc.AddMinutes(index * 15),
                startsAtUtc.AddMinutes((index + 1) * 15)))
            .ToArray();
    }

    private static decimal DeterminePrice(DateTimeOffset slotStartUtc, bool includeNegativePrices)
    {
        if (includeNegativePrices && slotStartUtc.Hour is >= 12 and < 15)
        {
            return -0.05m;
        }

        if (slotStartUtc.Hour is >= 18 and < 21)
        {
            return 0.48m;
        }

        if (slotStartUtc.Hour is >= 2 and < 5)
        {
            return 0.18m;
        }

        return 0.30m;
    }

    private static decimal DeterminePvYieldKwh(DateTimeOffset slotStartUtc)
    {
        return slotStartUtc.Hour switch
        {
            >= 12 and < 15 => 0.20m,
            >= 10 and < 17 => 0.35m,
            >= 8 and < 17 => 0.25m,
            _ => 0m
        };
    }

    private static decimal DetermineConsumptionKwh(DateTimeOffset slotStartUtc)
    {
        return slotStartUtc.Hour switch
        {
            >= 6 and < 9 => 0.45m,
            >= 17 and < 21 => 0.55m,
            >= 0 and < 5 => 0.12m,
            _ => 0.25m
        };
    }
}
