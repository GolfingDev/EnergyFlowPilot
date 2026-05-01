using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests.TestDoubles;

internal sealed class FakeTibberPriceForecastProvider : ITibberPriceForecastProvider
{
    public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var priceSlots = FifteenMinuteSlotFactory
            .CreateSlots(startsAtUtc, endsAtUtc)
            .Select(timeSlot => new TibberPriceForecastSlot(timeSlot, DeterminePricePerKwh(timeSlot.StartsAtUtc), "EUR"))
            .ToArray();

        return Task.FromResult<IReadOnlyList<TibberPriceForecastSlot>>(priceSlots);
    }

    private static decimal DeterminePricePerKwh(DateTimeOffset slotStartUtc)
    {
        var hourOfDayUtc = slotStartUtc.Hour;

        // The deterministic profile gives the Decision Engine cheap midday slots and expensive evening slots to reason about.
        return hourOfDayUtc switch
        {
            >= 0 and <= 5 => 0.22m,
            >= 6 and <= 8 => 0.34m,
            >= 9 and <= 15 => 0.18m,
            >= 16 and <= 20 => 0.39m,
            _ => 0.28m
        };
    }
}
