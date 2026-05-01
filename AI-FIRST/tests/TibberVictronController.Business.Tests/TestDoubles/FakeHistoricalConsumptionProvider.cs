using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests.TestDoubles;

internal sealed class FakeHistoricalConsumptionProvider : IHistoricalConsumptionProvider
{
    private static readonly decimal[] ThreePartyHouseholdHourlyConsumptionKwh =
    {
        0.45m, 0.35m, 0.30m, 0.30m, 0.35m, 0.55m,
        1.30m, 1.70m, 1.25m, 0.90m, 0.80m, 0.75m,
        0.90m, 0.85m, 0.80m, 0.95m, 1.25m, 1.85m,
        2.30m, 2.10m, 1.65m, 1.10m, 0.75m, 0.50m
    };

    public Task<IReadOnlyList<ConsumptionForecastSlot>> GetConsumptionForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var consumptionSlots = FifteenMinuteSlotFactory
            .CreateSlots(startsAtUtc, endsAtUtc)
            .Select(timeSlot => new ConsumptionForecastSlot(timeSlot, DetermineExpectedConsumptionKwh(timeSlot.StartsAtUtc)))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ConsumptionForecastSlot>>(consumptionSlots);
    }

    private static decimal DetermineExpectedConsumptionKwh(DateTimeOffset slotStartUtc)
    {
        var hourlyConsumptionKwh = ThreePartyHouseholdHourlyConsumptionKwh[slotStartUtc.Hour];

        // The hourly profile is split evenly into four 15-minute slots to keep the first test double transparent.
        return hourlyConsumptionKwh / 4m;
    }
}
