using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Consumption;

/// <summary>
/// Provides a first consumption forecast from an average three-party household profile.
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

    private readonly IControllerSettingStore controllerSettingStore;

    public AverageDailyConsumptionForecastProvider(IControllerSettingStore controllerSettingStore)
    {
        this.controllerSettingStore = controllerSettingStore;
    }

    /// <summary>
    /// Creates 15-minute consumption slots from the configured local daily average.
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

        for (var slotStartUtc = startsAtUtc; slotStartUtc < endsAtUtc; slotStartUtc = slotStartUtc.AddMinutes(15))
        {
            var slotEndUtc = slotStartUtc.AddMinutes(15);
            var localSlotStart = TimeZoneInfo.ConvertTime(slotStartUtc, timeZone);
            var hourlyConsumptionKwh = ThreePartyHouseholdHourlyConsumptionShare[localSlotStart.Hour] * scaleFactor;
            var slotConsumptionKwh = hourlyConsumptionKwh / 4m;

            consumptionSlots.Add(new ConsumptionForecastSlot(
                new ForecastTimeSlot(slotStartUtc, slotEndUtc),
                slotConsumptionKwh));
        }

        return consumptionSlots;
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
