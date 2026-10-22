using System.Collections.Concurrent;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Completes the Tibber price forecast up to the planning horizon with prices of a comparable day.
/// </summary>
/// <remarks>
/// Tibber publishes next-day prices around 13:00 local time. Before that, the plan would end at midnight and could neither
/// see the next morning's demand nor the next day's PV. Prices of a comparable day are usually close enough to plan holding;
/// grid charging is never planned on estimated prices, and the plan is recalculated as soon as real prices are published.
/// Comparable day: Tuesday to Friday use the previous day, Saturday, Sunday and Monday use the same weekday one week earlier.
/// Past prices come from the decision log, which stores the Tibber price of every decision.
/// </remarks>
public sealed class ComparableDayPriceEstimator
{
    private static readonly TimeSpan SlotDuration = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<DateTimeOffset, IReadOnlyDictionary<TimeSpan, decimal>> PastDayPriceCache = new();

    private readonly IElectricityPriceForecastProvider tibberPriceForecastProvider;
    private readonly IDecisionLogRepository decisionLogRepository;
    private readonly TimeZoneInfo timeZone;

    public ComparableDayPriceEstimator(
        IElectricityPriceForecastProvider tibberPriceForecastProvider,
        IDecisionLogRepository decisionLogRepository,
        TimeZoneInfo timeZone)
    {
        this.tibberPriceForecastProvider = tibberPriceForecastProvider;
        this.decisionLogRepository = decisionLogRepository;
        this.timeZone = timeZone;
    }

    /// <summary>
    /// Appends estimated 15-minute slots after the last known price until <paramref name="endsAtUtc"/>.
    /// </summary>
    public async Task<IReadOnlyList<TibberPriceForecastSlot>> CompleteAsync(
        IReadOnlyList<TibberPriceForecastSlot> knownSlots,
        DateTimeOffset endsAtUtc,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (knownSlots.Count == 0 || knownSlots[^1].TimeSlot.EndsAtUtc >= endsAtUtc)
        {
            return knownSlots;
        }

        var result = knownSlots.ToList();
        var currency = knownSlots[^1].Currency;
        var sourceDayPrices = new Dictionary<DateTime, IReadOnlyDictionary<TimeSpan, decimal>>();

        for (var slotStartUtc = knownSlots[^1].TimeSlot.EndsAtUtc; slotStartUtc.Add(SlotDuration) <= endsAtUtc; slotStartUtc = slotStartUtc.Add(SlotDuration))
        {
            var localStart = TimeZoneInfo.ConvertTime(slotStartUtc, timeZone);
            var sourceDay = GetComparableDay(localStart.Date);

            if (!sourceDayPrices.TryGetValue(sourceDay, out var prices))
            {
                prices = await LoadDayPricesAsync(sourceDay, nowUtc, cancellationToken);
                sourceDayPrices[sourceDay] = prices;
            }

            if (!prices.TryGetValue(localStart.TimeOfDay, out var price))
            {
                // Without a comparable price the plan ends here instead of guessing further.
                break;
            }

            // Negative prices of the comparable day are not repeated: negative-price rules must only act on published prices.
            result.Add(new TibberPriceForecastSlot(new ForecastTimeSlot(slotStartUtc, slotStartUtc.Add(SlotDuration)), Math.Max(0m, price), currency)
            {
                IsEstimated = true
            });
        }

        return result;
    }

    /// <summary>
    /// Tuesday to Friday follow the previous working day; Saturday, Sunday and Monday follow the same weekday one week earlier.
    /// </summary>
    public static DateTime GetComparableDay(DateTime localDate)
    {
        return localDate.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday or DayOfWeek.Friday
            ? localDate.AddDays(-1)
            : localDate.AddDays(-7);
    }

    private async Task<IReadOnlyDictionary<TimeSpan, decimal>> LoadDayPricesAsync(
        DateTime localDay,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var dayStartUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay, DateTimeKind.Unspecified), timeZone), TimeSpan.Zero);
        var dayEndUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay.AddDays(1), DateTimeKind.Unspecified), timeZone), TimeSpan.Zero);

        // The comparable day is often today, whose complete prices Tibber still delivers.
        var tibberSlots = await tibberPriceForecastProvider.GetPriceForecastAsync(dayStartUtc, dayEndUtc, cancellationToken);
        var tibberPrices = tibberSlots
            .Where(slot => slot.TimeSlot.StartsAtUtc >= dayStartUtc && slot.TimeSlot.StartsAtUtc < dayEndUtc)
            // The repeated autumn hour has two UTC slots per local quarter hour. For estimates use
            // their mean, independent of enumeration order; never turn these estimates into grid purchases.
            .GroupBy(slot => TimeZoneInfo.ConvertTime(slot.TimeSlot.StartsAtUtc, timeZone).TimeOfDay)
            .ToDictionary(group => group.Key, group => group.Average(slot => slot.TotalPricePerKwh));

        if (tibberPrices.Count > 0)
        {
            return tibberPrices;
        }

        // Legacy decision logs/cache contain Tibber prices. Never reuse them for another supplier.
        // Octopus estimates may use published comparable-day prices, but not untagged historical prices.
        if (tibberPriceForecastProvider.Source != "tibber")
            return new Dictionary<TimeSpan, decimal>();

        if (dayEndUtc > nowUtc)
        {
            return new Dictionary<TimeSpan, decimal>();
        }

        if (PastDayPriceCache.TryGetValue(dayStartUtc, out var cachedPrices))
        {
            return cachedPrices;
        }

        // One small indexed query per slot; past days never change, so the result is cached for the process lifetime.
        var loggedSamples = new List<(TimeSpan LocalTime, decimal Price)>();
        for (var slotStartUtc = dayStartUtc; slotStartUtc < dayEndUtc; slotStartUtc = slotStartUtc.Add(SlotDuration))
        {
            var decisions = await decisionLogRepository.GetDecisionsAsync(slotStartUtc, slotStartUtc.Add(SlotDuration).AddTicks(-1), 1, cancellationToken);
            var price = decisions.FirstOrDefault(decision => decision.TibberPricePerKwh is not null &&
                IsTibberHistory(decision.InputSummaryJson))?.TibberPricePerKwh;

            if (price is not null)
            {
                loggedSamples.Add((TimeZoneInfo.ConvertTime(slotStartUtc, timeZone).TimeOfDay, price.Value));
            }
        }

        foreach (var oldDay in PastDayPriceCache.Keys.Where(day => day < dayStartUtc.AddDays(-14)))
        {
            PastDayPriceCache.TryRemove(oldDay, out _);
        }

        var loggedPrices = loggedSamples.GroupBy(sample => sample.LocalTime)
            .ToDictionary(group => group.Key, group => group.Average(sample => sample.Price));
        PastDayPriceCache[dayStartUtc] = loggedPrices;
        return loggedPrices;
    }

    private static bool IsTibberHistory(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return !document.RootElement.TryGetProperty("ElectricityPriceProvider", out var source) ||
                source.GetString() == "tibber";
        }
        catch (JsonException) { return false; }
    }
}
