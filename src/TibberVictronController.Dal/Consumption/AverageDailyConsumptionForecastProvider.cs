using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Consumption;

/// <summary>
/// Builds a 15-minute consumption forecast from a learned, temperature-scaled daily profile and falls back to the initial
/// average-day shape when no measurements exist yet.
/// </summary>
public sealed class AverageDailyConsumptionForecastProvider : IHistoricalConsumptionProvider
{
    private static readonly decimal[] ThreePartyHouseholdHourlyConsumptionShare =
    {
        0.45m, 0.35m, 0.30m, 0.30m, 0.35m, 0.55m,
        1.30m, 1.70m, 1.25m, 0.90m, 0.80m, 0.75m,
        0.90m, 0.85m, 0.80m, 0.95m, 1.25m, 1.85m,
        2.30m, 2.10m, 1.65m, 1.10m, 0.75m, 0.50m
    };

    private static readonly decimal BaseDailyConsumptionKwh = ThreePartyHouseholdHourlyConsumptionShare.Sum();
    private static readonly TimeSpan SlotDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ProfileHistoryWindow = TimeSpan.FromDays(14);
    private static readonly TimeSpan TemperatureLookahead = TimeSpan.FromDays(4);

    private readonly IControllerSettingStore controllerSettingStore;
    private readonly ControllerDbContext dbContext;
    private readonly ITemperatureProvider temperatureProvider;
    private readonly ConsumptionForecastModelCache modelCache;
    private readonly ILogger<AverageDailyConsumptionForecastProvider> logger;

    public AverageDailyConsumptionForecastProvider(
        IControllerSettingStore controllerSettingStore,
        ControllerDbContext dbContext,
        ITemperatureProvider temperatureProvider,
        ConsumptionForecastModelCache modelCache,
        ILogger<AverageDailyConsumptionForecastProvider> logger)
    {
        this.controllerSettingStore = controllerSettingStore;
        this.dbContext = dbContext;
        this.temperatureProvider = temperatureProvider;
        this.modelCache = modelCache;
        this.logger = logger;
    }

    /// <summary>
    /// Creates 15-minute consumption slots from the learned profile and uses the initial average-day baseline as fallback.
    /// </summary>
    public async Task<IReadOnlyList<ConsumptionForecastSlot>> GetConsumptionForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtcRange(startsAtUtc, endsAtUtc);

        var averageDailyConsumptionKwh = await GetAverageDailyConsumptionKwhAsync(cancellationToken);
        var timeZone = await GetForecastTimeZoneAsync(cancellationToken);
        var scaleFactor = averageDailyConsumptionKwh / BaseDailyConsumptionKwh;
        var consumptionSlots = new List<ConsumptionForecastSlot>();
        var model = await GetOrBuildModelAsync(timeZone, cancellationToken);

        for (var slotStartUtc = startsAtUtc; slotStartUtc < endsAtUtc; slotStartUtc = slotStartUtc.Add(SlotDuration))
        {
            var slotEndUtc = slotStartUtc.Add(SlotDuration);
            var localSlotStart = TimeZoneInfo.ConvertTime(slotStartUtc, timeZone);
            var profileConsumptionKwh = model?.GetSlotConsumptionKwh(
                DateOnly.FromDateTime(localSlotStart.DateTime),
                GetSlotIndex(localSlotStart),
                SlotDuration);
            var slotConsumptionKwh = profileConsumptionKwh ?? GetFallbackSlotConsumptionKwh(localSlotStart, scaleFactor);

            consumptionSlots.Add(new ConsumptionForecastSlot(
                new ForecastTimeSlot(slotStartUtc, slotEndUtc),
                slotConsumptionKwh));
        }

        return consumptionSlots;
    }

    private async Task<ConsumptionForecastModel?> GetOrBuildModelAsync(
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var latestSampleMeasuredAtUtc = await dbContext.LiveConsumptionSamples
            .OrderByDescending(sample => sample.MeasuredAtUtc)
            .Select(sample => (DateTimeOffset?)sample.MeasuredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSampleMeasuredAtUtc is not { } latestSampleUtc)
        {
            return null;
        }

        return await modelCache.GetOrCreateAsync(
            timeZone.Id,
            latestSampleUtc,
            token => BuildModelAsync(timeZone, latestSampleUtc, token),
            cancellationToken);
    }

    private async Task<ConsumptionForecastModel> BuildModelAsync(
        TimeZoneInfo timeZone,
        DateTimeOffset latestSampleUtc,
        CancellationToken cancellationToken)
    {
        // A short history window follows seasonal load changes such as a heat pump quickly.
        var historyStartsAtUtc = latestSampleUtc.Subtract(ProfileHistoryWindow);
        var rawSamples = await dbContext.LiveConsumptionSamples
            .Where(sample => sample.MeasuredAtUtc >= historyStartsAtUtc)
            .Select(sample => new { sample.MeasuredAtUtc, sample.HouseConsumptionWatts })
            .ToArrayAsync(cancellationToken);

        var history = rawSamples
            .Select(sample => new
            {
                LocalMeasuredAt = TimeZoneInfo.ConvertTime(sample.MeasuredAtUtc, timeZone),
                EffectiveConsumptionWatts = Math.Max(0m, sample.HouseConsumptionWatts)
            })
            .GroupBy(sample => new
            {
                LocalDate = DateOnly.FromDateTime(sample.LocalMeasuredAt.DateTime),
                SlotIndex = GetSlotIndex(sample.LocalMeasuredAt)
            })
            .Select(sampleGroup => new ConsumptionDaySlotAverage(
                sampleGroup.Key.LocalDate,
                sampleGroup.Key.SlotIndex,
                sampleGroup.Average(sample => sample.EffectiveConsumptionWatts)))
            .ToArray();

        // The temperature window reaches beyond the planning horizon so future days get their own heating scale.
        var temperatures = await temperatureProvider.GetHourlyTemperaturesAsync(
            historyStartsAtUtc.Subtract(TimeSpan.FromDays(1)),
            latestSampleUtc.Add(TemperatureLookahead),
            cancellationToken);
        var heatingDegrees = ConsumptionForecastModel.CalculateHeatingDegrees(temperatures, timeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(latestSampleUtc, timeZone).DateTime);
        var model = ConsumptionForecastModel.Build(history, heatingDegrees, today);

        logger.LogInformation(
            "Verbrauchsprofil neu berechnet: {Days} vollstaendige Tage, {TemperatureDays} Tage mit Temperatur, Grundlast {BaseDailyKwh} kWh/Tag, Heizanteil {SlopeKwh} kWh je Heizgrad.",
            model.CompleteDays,
            heatingDegrees.Count,
            model.BaseDailyKwh is { } baseKwh ? Math.Round(baseKwh, 2) : (decimal?)null,
            model.HeatingSlopeKwhPerDegree is { } slope ? Math.Round(slope, 3) : (decimal?)null);

        return model;
    }

    private async Task<decimal> GetAverageDailyConsumptionKwhAsync(CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.ConsumptionForecastAverageDailyConsumptionKwhKey,
            cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException("Der durchschnittliche Tagesverbrauch ist nicht konfiguriert.");
        }

        if (!decimal.TryParse(setting.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var averageDailyConsumptionKwh))
        {
            throw new InvalidOperationException("Der durchschnittliche Tagesverbrauch muss als Dezimalzahl konfiguriert sein.");
        }

        if (averageDailyConsumptionKwh <= 0m)
        {
            throw new InvalidOperationException("Der durchschnittliche Tagesverbrauch muss groesser als 0 kWh sein.");
        }

        return averageDailyConsumptionKwh;
    }

    private async Task<TimeZoneInfo> GetForecastTimeZoneAsync(CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.ConsumptionForecastTimeZoneKey,
            cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException("Die Verbrauchsforecast-Zeitzone ist nicht konfiguriert.");
        }

        return ResolveTimeZone(setting.Value!);
    }

    private static decimal GetFallbackSlotConsumptionKwh(DateTimeOffset localSlotStart, decimal scaleFactor)
    {
        var hourlyConsumptionKwh = ThreePartyHouseholdHourlyConsumptionShare[localSlotStart.Hour] * scaleFactor;
        return hourlyConsumptionKwh / 4m;
    }

    private static int GetSlotIndex(DateTimeOffset localMeasuredAt)
    {
        return localMeasuredAt.Hour * 4 + localMeasuredAt.Minute / 15;
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "Europe/Berlin", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidOperationException("Die Verbrauchsforecast-Zeitzone ist ungueltig.", exception);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidOperationException("Die Verbrauchsforecast-Zeitzone wurde auf diesem System nicht gefunden.", exception);
        }
    }

    private static void ValidateUtcRange(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Start des Verbrauchsforecast-Zeitraums muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        if (endsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Das Ende des Verbrauchsforecast-Zeitraums muss in UTC angegeben sein.", nameof(endsAtUtc));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Das Ende des Verbrauchsforecast-Zeitraums muss nach dem Start liegen.", nameof(endsAtUtc));
        }
    }
}
