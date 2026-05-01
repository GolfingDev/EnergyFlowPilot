using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests.TestDoubles;

internal sealed class FakeWeatherForecastProvider : IWeatherForecastProvider
{
    public Task<IReadOnlyList<PvYieldForecastSlot>> GetPvYieldForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pvYieldSlots = FifteenMinuteSlotFactory
            .CreateSlots(startsAtUtc, endsAtUtc)
            .Select(timeSlot => new PvYieldForecastSlot(timeSlot, DetermineExpectedPvYieldKwh(timeSlot.StartsAtUtc)))
            .ToArray();

        return Task.FromResult<IReadOnlyList<PvYieldForecastSlot>>(pvYieldSlots);
    }

    private static decimal DetermineExpectedPvYieldKwh(DateTimeOffset slotStartUtc)
    {
        var hourOfDayUtc = slotStartUtc.Hour;

        // This simple sunny-day shape totals about 12 kWh and is only meant as deterministic Decision Engine test input.
        return hourOfDayUtc switch
        {
            >= 6 and <= 7 => 0.05m,
            >= 8 and <= 10 => 0.18m,
            >= 11 and <= 14 => 0.42m,
            >= 15 and <= 17 => 0.20m,
            >= 18 and <= 19 => 0.04m,
            _ => 0.00m
        };
    }
}
