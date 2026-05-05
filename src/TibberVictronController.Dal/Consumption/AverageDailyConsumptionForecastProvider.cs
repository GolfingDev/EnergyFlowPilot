using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Consumption;

/// <summary>
/// Builds a 15-minute consumption forecast from persisted weekday profiles and falls back to the initial average-day shape when needed.
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

    private readonly IControllerSettingStore controllerSettingStore;
    private readonly ControllerDbContext dbContext;

    public AverageDailyConsumptionForecastProvider(
        IControllerSettingStore controllerSettingStore,
        ControllerDbContext dbContext)
    {
        this.controllerSettingStore = controllerSettingStore;
        this.dbContext = dbContext;
    }

    /// <summary>
    /// Creates 15-minute consumption slots from persisted weekday profiles and uses the initial average-day baseline as fallback.
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
        var profiles = await GetOrBuildProfilesAsync(timeZone, cancellationToken);
        var profilesByDayAndSlot = profiles.ToDictionary(
            profile => (profile.DayOfWeek, profile.SlotIndex),
            profile => profile.AverageConsumptionWatts);
        var slotAverages = profiles
            .GroupBy(profile => profile.SlotIndex)
            .ToDictionary(
                profileGroup => profileGroup.Key,
                profileGroup => profileGroup.Average(profile => profile.AverageConsumptionWatts));

        for (var slotStartUtc = startsAtUtc; slotStartUtc < endsAtUtc; slotStartUtc = slotStartUtc.Add(SlotDuration))
        {
            var slotEndUtc = slotStartUtc.Add(SlotDuration);
            var localSlotStart = TimeZoneInfo.ConvertTime(slotStartUtc, timeZone);
            var slotIndex = GetSlotIndex(localSlotStart);
            var profileConsumptionKwh = TryGetProfileConsumptionKwh(
                profilesByDayAndSlot,
                slotAverages,
                localSlotStart.DayOfWeek,
                slotIndex);
            var fallbackConsumptionKwh = GetFallbackSlotConsumptionKwh(localSlotStart, scaleFactor);
            var slotConsumptionKwh = profileConsumptionKwh ?? fallbackConsumptionKwh;

            consumptionSlots.Add(new ConsumptionForecastSlot(
                new ForecastTimeSlot(slotStartUtc, slotEndUtc),
                slotConsumptionKwh));
        }

        return consumptionSlots;
    }

    private async Task<List<ConsumptionDayProfileEntity>> GetOrBuildProfilesAsync(
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var latestSampleMeasuredAtUtc = await dbContext.LiveConsumptionSamples
            .OrderByDescending(sample => sample.MeasuredAtUtc)
            .Select(sample => (DateTimeOffset?)sample.MeasuredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSampleMeasuredAtUtc is null)
        {
            return new List<ConsumptionDayProfileEntity>();
        }

        var existingProfiles = await dbContext.ConsumptionDayProfiles
            .OrderBy(profile => profile.DayOfWeek)
            .ThenBy(profile => profile.SlotIndex)
            .ToListAsync(cancellationToken);

        if (existingProfiles.Count > 0 &&
            existingProfiles.All(profile => profile.UpdatedAtUtc >= latestSampleMeasuredAtUtc.Value))
        {
            return existingProfiles;
        }

        var rawSamples = await dbContext.LiveConsumptionSamples
            .OrderBy(sample => sample.MeasuredAtUtc)
            .ToArrayAsync(cancellationToken);

        var updatedAtUtc = latestSampleMeasuredAtUtc.Value;
        var rebuiltProfiles = rawSamples
            .Select(sample => new
            {
                LocalMeasuredAt = TimeZoneInfo.ConvertTime(sample.MeasuredAtUtc, timeZone),
                EffectiveConsumptionWatts = Math.Max(0m, sample.HouseConsumptionWatts)
            })
            .GroupBy(sample => new
            {
                DayOfWeek = (int)sample.LocalMeasuredAt.DayOfWeek,
                SlotIndex = GetSlotIndex(sample.LocalMeasuredAt)
            })
            .Select(sampleGroup => new ConsumptionDayProfileEntity
            {
                DayOfWeek = sampleGroup.Key.DayOfWeek,
                SlotIndex = sampleGroup.Key.SlotIndex,
                AverageConsumptionWatts = sampleGroup.Average(sample => sample.EffectiveConsumptionWatts),
                SampleCount = sampleGroup.Count(),
                UpdatedAtUtc = updatedAtUtc
            })
            .ToList();

        dbContext.ConsumptionDayProfiles.RemoveRange(dbContext.ConsumptionDayProfiles);
        dbContext.ConsumptionDayProfiles.AddRange(rebuiltProfiles);
        await dbContext.SaveChangesAsync(cancellationToken);

        return rebuiltProfiles;
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

    private static decimal? TryGetProfileConsumptionKwh(
        IReadOnlyDictionary<(int DayOfWeek, int SlotIndex), decimal> profilesByDayAndSlot,
        IReadOnlyDictionary<int, decimal> slotAverages,
        DayOfWeek dayOfWeek,
        int slotIndex)
    {
        if (profilesByDayAndSlot.TryGetValue(((int)dayOfWeek, slotIndex), out var profileWatts))
        {
            return ConvertWattsToSlotConsumptionKwh(profileWatts);
        }

        if (slotAverages.TryGetValue(slotIndex, out var fallbackWatts))
        {
            return ConvertWattsToSlotConsumptionKwh(fallbackWatts);
        }

        return null;
    }

    private static decimal GetFallbackSlotConsumptionKwh(DateTimeOffset localSlotStart, decimal scaleFactor)
    {
        var hourlyConsumptionKwh = ThreePartyHouseholdHourlyConsumptionShare[localSlotStart.Hour] * scaleFactor;
        return hourlyConsumptionKwh / 4m;
    }

    private static decimal ConvertWattsToSlotConsumptionKwh(decimal watts)
    {
        return watts / 1000m * (decimal)SlotDuration.TotalHours;
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
