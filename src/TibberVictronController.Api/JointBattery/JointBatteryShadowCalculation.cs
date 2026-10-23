using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Loads independent live inputs for the shadow planner; never resolves a command publisher.</summary>
public sealed class JointBatteryShadowCalculation(IServiceProvider services)
{
    public async Task<JointBatteryPlan> CalculateAsync(E3dcLocalTelemetry e3dc, CancellationToken token)
    {
        var clock = services.GetRequiredService<IUtcClock>();
        var settings = services.GetRequiredService<IControllerSettingStore>();
        var now = clock.UtcNow;
        async Task<decimal> Setting(string key) => decimal.Parse(
            (await settings.GetSettingAsync(key, token))?.Value ?? throw new InvalidOperationException("Eine Einstellung fuer die gemeinsame Vorschau fehlt."),
            CultureInfo.InvariantCulture);
        var mqtt = services.GetRequiredService<MqttTelemetrySnapshotStore>().GetSnapshot();
        foreach (var date in new[] { mqtt.BatterySocMeasuredAtUtc, mqtt.BatteryPowerMeasuredAtUtc, mqtt.GridPowerMeasuredAtUtc })
            if (date is null || now - date.Value > TimeSpan.FromSeconds(30) || date > now.AddSeconds(5))
                throw new InvalidOperationException("Die gemeinsame Vorschau benoetigt frische Victron-Messwerte.");
        if (mqtt.BatterySocPercent is null || mqtt.BatteryPowerWatts is null || mqtt.GridPowerWatts is null)
            throw new InvalidOperationException("Fuer die gemeinsame Vorschau fehlen Victron-Messwerte.");
        var primary = await services.GetRequiredService<IBatteryConfigurationProvider>().GetBatteryConfigurationAsync(token);
        var reservePercent = e3dc.ReserveKwh / e3dc.UsableCapacityKwh * 100m;
        var secondary = new BatteryConfiguration(new BatteryConfigurationValues {
            TotalCapacityKwh = e3dc.UsableCapacityKwh, MinimumStateOfChargePercent = reservePercent,
            PlanningMinimumStateOfChargePercent = reservePercent,
            PlanningMaximumStateOfChargePercent = await Setting(ControllerSettingDefaults.E3dcPlanningMaximumSocKey),
            MaximumChargePowerWatts = e3dc.MaximumChargeWatts,
            MaximumDischargePowerWatts = e3dc.MaximumDischargeWatts,
            RoundTripEfficiencyPercent = await Setting(ControllerSettingDefaults.E3dcEfficiencyKey),
            MinimumSavingsPerKwh = primary.MinimumSavingsPerKwh, WearCostPerKwh = primary.WearCostPerKwh
        });
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0, TimeSpan.Zero);
        var prices = (await services.GetRequiredService<IElectricityPriceForecastProvider>().GetPriceForecastAsync(start, start.AddHours(48), token))
            .Where(p => p.TimeSlot.StartsAtUtc >= start && p.TimeSlot.EndsAtUtc <= start.AddHours(48)).OrderBy(p => p.TimeSlot.StartsAtUtc).ToArray();
        if (prices.Length == 0 || prices[0].TimeSlot.StartsAtUtc != start ||
            prices.Zip(prices.Skip(1)).Any(pair => pair.First.TimeSlot.EndsAtUtc != pair.Second.TimeSlot.StartsAtUtc))
            throw new InvalidOperationException("Fuer die gemeinsame Vorschau fehlen lueckenlose aktuelle Preise.");
        var end = prices[^1].TimeSlot.EndsAtUtc;
        var pv = await services.GetRequiredService<IWeatherForecastProvider>().GetPvYieldForecastAsync(start, end, token);
        var history = await services.GetRequiredService<ILiveConsumptionRepository>().GetSamplesAsync(now.AddDays(-14), now, token);
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await settings.GetSettingAsync(ControllerSettingDefaults.ConsumptionForecastTimeZoneKey, token))?.Value ?? "Europe/Berlin");
        IReadOnlyList<HourlyTemperature>? temperatures = null;
        try { temperatures = await services.GetRequiredService<ITemperatureProvider>().GetHourlyTemperaturesAsync(now.AddDays(-14), end.AddDays(1), token); }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        var model = JointConsumptionProfile.Learn(history, zone, now, temperatures);
        var fallbackDaily = await Setting(ControllerSettingDefaults.ConsumptionForecastAverageDailyConsumptionKwhKey);
        var consumption = prices.Select(price => {
            var local = TimeZoneInfo.ConvertTime(price.TimeSlot.StartsAtUtc, zone);
            return new ConsumptionForecastSlot(price.TimeSlot, model.GetSlotConsumptionKwh(DateOnly.FromDateTime(local.DateTime),
                local.Hour * 4 + local.Minute / 15, price.TimeSlot.Duration) ?? fallbackDaily / 24m * (decimal)price.TimeSlot.Duration.TotalHours);
        }).ToArray();
        var grossNetLoadWatts = mqtt.GridPowerWatts.Value - mqtt.BatteryPowerWatts.Value - e3dc.BatteryPowerWatts;
        var adjusted = BatteryForecastService.ApplyCurrentNetLoad(consumption, pv, start, grossNetLoadWatts);
        if (clock.UtcNow >= start.AddMinutes(15))
            throw new InvalidOperationException("Der Preiszeitabschnitt ist waehrend der Vorschau abgelaufen. Die Planung wird erneut angefordert.");
        var plan = new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = clock.UtcNow, Batteries = [new("victron", primary, new(mqtt.BatterySocPercent.Value, mqtt.BatterySocMeasuredAtUtc!.Value), (int)mqtt.BatteryPowerWatts.Value),
                new("e3dc", secondary, new(e3dc.SocPercent, e3dc.MeasuredAtUtc), e3dc.BatteryPowerWatts,
                    e3dc.EffectiveChargeLimitWatts, e3dc.EffectiveDischargeLimitWatts)],
            Prices = prices, Pv = pv, Consumption = adjusted,
            MaximumGridImportWatts = checked((int)await Setting(ControllerSettingDefaults.JointBatteryGridLimitKey)),
            FeedInCompensationPerKwh = await Setting(ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey)
        }, token);
        return plan with { Warnings = [.. plan.Warnings,
            .. (model.HasProfile ? Array.Empty<string>() : new[] { "Verbrauchsvorschau verwendet mangels geeigneter Messdaten ein gleichmaessiges Ersatzprofil." }),
            .. (temperatures is null ? new[] { "Temperaturdaten fehlen; gemeinsamer Verbrauchsforecast ohne Temperaturkorrektur." } : Array.Empty<string>()),
            "Der E3/DC-Wirkungsgrad ist eine konfigurierbare Annahme, kein ausgelesener Messwert." ] };
    }
}
