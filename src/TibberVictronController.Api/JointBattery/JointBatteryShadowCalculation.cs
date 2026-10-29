using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Dal.Mqtt;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Loads independent live inputs for the shadow planner; sends only MQTT read requests, never device commands.</summary>
public sealed class JointBatteryShadowCalculation(IServiceProvider services)
{
    private static string Local(DateTimeOffset? utc) => utc is { } value
        ? TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture)
        : "unbekannt";

    /// <summary>
    /// The Victron connection runs with "suppress-republish": values are only published when they change. An idle
    /// battery's SoC can therefore be minutes old although it is current. Explicit read requests refresh exactly the
    /// timestamps the planner checks, so the freshness rule stays strict without rejecting a quiet battery.
    /// </summary>
    private async Task<MqttTelemetrySnapshot> ReadFreshVictronSnapshotAsync(CancellationToken token)
    {
        var store = services.GetRequiredService<MqttTelemetrySnapshotStore>();
        var client = services.GetService<IVictronMqttControlClient>();
        var settingsProvider = services.GetService<DatabaseVictronMqttSettingsProvider>();
        if (client is null || settingsProvider is null) return store.GetSnapshot();

        var topics = VictronMqttTopicFactory.Create(await settingsProvider.GetSettingsAsync(token));
        var requestedAt = DateTimeOffset.UtcNow;
        foreach (var topic in new[] { topics.BatterySocTopic, topics.BatteryPowerTopic, topics.GridPowerTopic })
            await client.RequestValueAsync(topic, token);

        // Wall-clock wait (not IUtcClock): the answers are stamped with the receive time of the MQTT client.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            var snapshot = store.GetSnapshot();
            if (new[] { snapshot.BatterySocMeasuredAtUtc, snapshot.BatteryPowerMeasuredAtUtc, snapshot.GridPowerMeasuredAtUtc }
                .All(at => at >= requestedAt))
                return snapshot;
            try { await Task.Delay(100, wait.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // No answer in time: the caller's freshness check reports which value is missing.
                return store.GetSnapshot();
            }
        }
    }

    /// <summary>
    /// Loads the slow, slowly changing inputs first (prices, PV, 14-day history, temperatures) and reads both
    /// batteries only immediately before planning. On the Raspberry Pi the slow part can exceed the planner's
    /// 30 s freshness limit, so measurements taken up front were always stale by the time the plan was computed.
    /// </summary>
    public async Task<(JointBatteryPlan Plan, E3dcLocalTelemetry Telemetry)> CalculateAsync(CancellationToken token)
    {
        var clock = services.GetRequiredService<IUtcClock>();
        var settings = services.GetRequiredService<IControllerSettingStore>();
        var now = clock.UtcNow;
        async Task<decimal> Setting(string key) => decimal.Parse(
            (await settings.GetSettingAsync(key, token))?.Value ?? throw new InvalidOperationException("Eine Einstellung fuer die gemeinsame Vorschau fehlt."),
            CultureInfo.InvariantCulture);

        // 1. Slow inputs without live battery data. Operator rule: a failing forecast or data source degrades the plan
        //    (substitute + visible warning) but never stops it - see JointPlanInputFallback.
        var cache = services.GetService<JointBatteryInputCache>() ?? new JointBatteryInputCache();
        var inputWarnings = new List<string>();
        var primary = await services.GetRequiredService<IBatteryConfigurationProvider>().GetBatteryConfigurationAsync(token);
        var e3dcMaximumSoc = await Setting(ControllerSettingDefaults.E3dcPlanningMaximumSocKey);
        var e3dcEfficiency = await Setting(ControllerSettingDefaults.E3dcEfficiencyKey);
        var gridLimit = checked((int)await Setting(ControllerSettingDefaults.JointBatteryGridLimitKey));
        var feedInCompensation = await Setting(ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey);
        var start = JointPlanInputFallback.QuarterStart(now);
        var horizonEnd = start.AddHours(48);

        IReadOnlyList<TibberPriceForecastSlot>? freshPrices = null;
        try
        {
            freshPrices = await services.GetRequiredService<IElectricityPriceForecastProvider>().GetPriceForecastAsync(start, horizonEnd, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            inputWarnings.Add($"Preisabruf fehlgeschlagen ({exception.GetType().Name}); letzte bekannte Preise verwendet.");
        }
        var (loadedPrices, pricesFromCache) = JointPlanInputFallback.ChoosePrices(freshPrices, cache.Prices.Items, start, horizonEnd);
        if (loadedPrices.Count == 0)
            // Without any price for the current quarter hour there is nothing to optimise; execution then runs the
            // self-consumption fallback, so the batteries still act.
            throw new InvalidOperationException("Fuer die aktuelle Viertelstunde liegt kein Strompreis vor, auch kein zuvor geladener.");
        if (pricesFromCache) inputWarnings.Add($"Aktuelle Preise nicht verfuegbar; Preise vom {Local(cache.Prices.LoadedAtUtc)} verwendet.");
        else cache.StorePrices(freshPrices!, now);
        var prices = loadedPrices.ToArray();
        var end = prices[^1].TimeSlot.EndsAtUtc;
        if (end < horizonEnd.AddHours(-24)) inputWarnings.Add($"Preise liegen nur bis {Local(end)} lueckenlos vor; der Planungshorizont ist entsprechend kuerzer.");

        var weather = services.GetRequiredService<IWeatherForecastProvider>();
        IReadOnlyList<PvYieldForecastSlot>? freshPv = null;
        string? pvFailure = null;
        try { freshPv = await weather.GetPvYieldForecastAsync(start, end, token); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Forecast.Solar texts carry status codes or API messages only; other exception texts may contain URLs.
            pvFailure = exception is TibberVictronController.Dal.Weather.ForecastSolarApiException ? exception.Message : exception.GetType().Name;
        }
        if (freshPv is not null) cache.StorePv(freshPv, now);
        var (pv, pvFromCache, pvMissing) = JointPlanInputFallback.CompletePv(prices, freshPv, cache.Pv.Items);
        if (pvFailure is not null || pvFromCache > 0 || pvMissing > 0)
            inputWarnings.Add("PV-Prognose " + (pvFailure is null ? "unvollstaendig" : $"nicht verfuegbar ({pvFailure})") +
                (pvFromCache > 0 ? $"; {pvFromCache} Viertelstunden aus der Prognose vom {Local(cache.Pv.LoadedAtUtc)}" : "") +
                (pvMissing > 0 ? $"; {pvMissing} Viertelstunden ohne PV angesetzt (realer Ueberschuss wird trotzdem gespeichert)" : "") + ".");

        IReadOnlyList<LiveConsumptionSample> history = [];
        try { history = await services.GetRequiredService<ILiveConsumptionRepository>().GetSamplesAsync(now.AddDays(-14), now, token); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            inputWarnings.Add($"Verbrauchshistorie nicht lesbar ({exception.GetType().Name}); Ersatzprofil aus dem Tagesverbrauch verwendet.");
        }
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await settings.GetSettingAsync(ControllerSettingDefaults.ConsumptionForecastTimeZoneKey, token))?.Value ?? "Europe/Berlin");
        IReadOnlyList<HourlyTemperature>? temperatures = null;
        try { temperatures = await services.GetRequiredService<ITemperatureProvider>().GetHourlyTemperaturesAsync(now.AddDays(-14), end.AddHours(24), token); }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        var model = JointConsumptionProfile.Learn(history, zone, now, temperatures);
        var fallbackDaily = await Setting(ControllerSettingDefaults.ConsumptionForecastAverageDailyConsumptionKwhKey);
        var consumption = prices.Select(price => {
            var local = TimeZoneInfo.ConvertTime(price.TimeSlot.StartsAtUtc, zone);
            return new ConsumptionForecastSlot(price.TimeSlot, model.GetSlotConsumptionKwh(DateOnly.FromDateTime(local.DateTime),
                local.Hour * 4 + local.Minute / 15, price.TimeSlot.Duration) ?? fallbackDaily / 24m * (decimal)price.TimeSlot.Duration.TotalHours);
        }).ToArray();
        JointBatteryContinuation? continuation = null;
        string? continuationWarning = null;
        try
        {
            var localToday = TimeZoneInfo.ConvertTime(now, zone).Date;
            DateTimeOffset MidnightUtc(DateTime date) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            var selectedPrices = services.GetRequiredService<IElectricityPriceForecastProvider>();
            var historyStart = MidnightUtc(localToday.AddDays(-14));
            var historyEnd = MidnightUtc(localToday);
            // Generic tariffs can reconstruct past retail prices using the current tariff, avoiding mixed old markups.
            IReadOnlyList<TibberPriceForecastSlot> historical;
            if (selectedPrices.Source == "energyCharts")
                historical = await selectedPrices.GetPriceForecastAsync(historyStart, historyEnd, token);
            else
            {
                if (DateTimeOffset.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.ElectricityPriceProviderChangedAtKey, token))?.Value, out var changed) && changed > historyStart)
                    historyStart = changed.ToUniversalTime();
                historical = historyStart >= historyEnd ? [] : await services.GetRequiredService<JointBatteryPriceHistory>().GetAsync(
                    services.GetRequiredService<IDecisionLogRepository>(), new(historyStart, historyEnd, selectedPrices.Source, prices[0].Currency), token);
            }
            // This extra day carries no invented executable prices: only its net demand caps the terminal energy value.
            var futurePv = (await weather.GetPvYieldForecastAsync(end, end.AddHours(24), token)).ToDictionary(p => p.TimeSlot);
            var followingDay = new List<(decimal ConsumptionKwh, decimal PvKwh)>();
            for (var at = end; at < end.AddHours(24); at = at.AddMinutes(15))
            {
                var slot = new ForecastTimeSlot(at, at.AddMinutes(15));
                var local = TimeZoneInfo.ConvertTime(at, zone);
                if (!futurePv.TryGetValue(slot, out var yield) || !yield.IsForecastAvailable) break;
                var demand = model.GetSlotConsumptionKwh(DateOnly.FromDateTime(local.DateTime), local.Hour * 4 + local.Minute / 15, slot.Duration)
                    ?? fallbackDaily / 96m;
                followingDay.Add((demand, yield.ExpectedPvYieldKwh));
            }
            continuation = JointBatteryContinuationEstimator.Estimate(historical, zone, followingDay);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Supplemental history/PV must not disable the existing live plan or bypass freshness checks.
            continuationWarning = "Restenergiebewertung nicht verfuegbar; bestehende Planung ohne zusaetzliche Bevorratung.";
        }
        var loadDuration = clock.UtcNow - now;

        // 2. Live readings of both batteries, as late as possible.
        var e3dc = await services.GetRequiredService<IE3dcTelemetrySource>().ReadAsync(token);
        var mqtt = await ReadFreshVictronSnapshotAsync(token);
        var checkedAt = clock.UtcNow;
        foreach (var (date, name) in new[] { (mqtt.BatterySocMeasuredAtUtc, "Ladestand"), (mqtt.BatteryPowerMeasuredAtUtc, "Akkuleistung"),
            (mqtt.GridPowerMeasuredAtUtc, "Netzleistung") })
            if (date is null || checkedAt - date.Value > TimeSpan.FromSeconds(30) || date > checkedAt.AddSeconds(5))
                throw new InvalidOperationException($"Die gemeinsame Vorschau benoetigt frische Victron-Messwerte ({name} fehlt oder ist aelter als 30 Sekunden).");
        if (mqtt.BatterySocPercent is null || mqtt.BatteryPowerWatts is null || mqtt.GridPowerWatts is null)
            throw new InvalidOperationException("Fuer die gemeinsame Vorschau fehlen Victron-Messwerte.");

        // 3. Plan from the fresh readings.
        var reservePercent = e3dc.ReserveKwh / e3dc.UsableCapacityKwh * 100m;
        var secondary = new BatteryConfiguration(new BatteryConfigurationValues {
            TotalCapacityKwh = e3dc.UsableCapacityKwh, MinimumStateOfChargePercent = reservePercent,
            PlanningMinimumStateOfChargePercent = reservePercent,
            PlanningMaximumStateOfChargePercent = e3dcMaximumSoc,
            MaximumChargePowerWatts = e3dc.MaximumChargeWatts,
            MaximumDischargePowerWatts = e3dc.MaximumDischargeWatts,
            RoundTripEfficiencyPercent = e3dcEfficiency,
            MinimumSavingsPerKwh = primary.MinimumSavingsPerKwh, WearCostPerKwh = primary.WearCostPerKwh
        });
        var grossNetLoadWatts = mqtt.GridPowerWatts.Value - mqtt.BatteryPowerWatts.Value - e3dc.BatteryPowerWatts;
        // Loading the inputs takes 25-30 s on the Pi and can cross a quarter-hour boundary (33 discarded plans on
        // 28./29.10.2026). The inputs are still valid, so the plan simply starts at the quarter hour that is now running.
        var planStart = JointPlanInputFallback.QuarterStart(clock.UtcNow);
        var plannedPrices = prices.Where(p => p.TimeSlot.StartsAtUtc >= planStart).ToArray();
        if (plannedPrices.Length == 0 || plannedPrices[0].TimeSlot.StartsAtUtc != planStart)
            throw new InvalidOperationException("Fuer die aktuelle Viertelstunde liegt kein Strompreis vor, auch kein zuvor geladener.");
        var adjusted = BatteryForecastService.ApplyCurrentNetLoad(consumption, pv, planStart, grossNetLoadWatts);
        var (e3dcCharge, e3dcDischarge) = await E3dcLimitPolicy.ActiveLimitsAsync(settings, e3dc, token);
        var plan = new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = clock.UtcNow, Batteries = [new("victron", primary, new(mqtt.BatterySocPercent.Value, mqtt.BatterySocMeasuredAtUtc!.Value), (int)mqtt.BatteryPowerWatts.Value),
                new("e3dc", secondary, new(e3dc.SocPercent, e3dc.MeasuredAtUtc), e3dc.BatteryPowerWatts, e3dcCharge, e3dcDischarge)],
            Prices = plannedPrices, Pv = pv, Consumption = adjusted,
            MaximumGridImportWatts = gridLimit,
            FeedInCompensationPerKwh = feedInCompensation,
            Continuation = continuation
        }, token);
        return (plan with { PriceSource = services.GetRequiredService<IElectricityPriceForecastProvider>().Source,
            Warnings = [.. plan.Warnings,
            .. inputWarnings,
            .. (continuationWarning is null ? Array.Empty<string>() : new[] { continuationWarning }),
            .. (model.HasProfile ? Array.Empty<string>() : new[] { "Verbrauchsvorschau verwendet mangels geeigneter Messdaten ein gleichmaessiges Ersatzprofil." }),
            .. (temperatures is null ? new[] { "Temperaturdaten fehlen; gemeinsamer Verbrauchsforecast ohne Temperaturkorrektur." } : Array.Empty<string>()),
            .. (loadDuration > TimeSpan.FromSeconds(20)
                ? new[] { $"Laden der Prognosedaten dauerte {loadDuration.TotalSeconds:F0} Sekunden." } : Array.Empty<string>()),
            "Der E3/DC-Wirkungsgrad ist eine konfigurierbare Annahme, kein ausgelesener Messwert." ] }, e3dc);
    }
}
