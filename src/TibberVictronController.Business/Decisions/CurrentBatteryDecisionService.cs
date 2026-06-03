using System.Globalization;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Validates live inputs and calculates the current direct Decision Engine result for real control decisions.
/// </summary>
public sealed class CurrentBatteryDecisionService : ICurrentBatteryDecisionService
{
    private static readonly TimeSpan BatteryStateMaxAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SiteTelemetryMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DecisionLookahead = TimeSpan.FromHours(24);
    private const int MaximumPlausiblePowerWatts = 100_000;

    private readonly CurrentBatteryDecisionServiceDependencies dependencies;
    private readonly TibberPriceDecisionRule tibberPriceDecisionRule = new();
    private readonly BatteryDischargePowerLimiter dischargePowerLimiter = new();
    private readonly BatteryGridExportAbsorptionPolicy gridExportAbsorptionPolicy = new();

    public CurrentBatteryDecisionService(CurrentBatteryDecisionServiceDependencies dependencies)
    {
        this.dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies), "Die Abhaengigkeiten fuer die Direktentscheidung duerfen nicht null sein.");
    }

    public async Task<CurrentBatteryDecisionResult> CalculateCurrentDecisionAsync(CancellationToken cancellationToken = default)
    {
        var decidedAtUtc = dependencies.UtcClock.UtcNow;
        var batteryConfiguration = await dependencies.BatteryConfigurationProvider.GetBatteryConfigurationAsync(cancellationToken);
        var batteryStateResult = await TryGetBatteryStateAsync(decidedAtUtc, cancellationToken);
        var siteTelemetryResult = await TryGetSiteTelemetryAsync(decidedAtUtc, cancellationToken);

        if (!batteryStateResult.IsSuccess)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryStateResult.BatteryState,
                siteTelemetryResult.SiteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                batteryStateResult.Reason!,
                cancellationToken);
        }

        if (!siteTelemetryResult.IsSuccess)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryStateResult.BatteryState,
                siteTelemetryResult.SiteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                siteTelemetryResult.Reason!,
                cancellationToken);
        }

        var batteryState = batteryStateResult.BatteryState;
        var siteTelemetry = siteTelemetryResult.SiteTelemetry;
        var manualChargeOverride = await TryGetManualChargeOverrideAsync(decidedAtUtc, batteryConfiguration, cancellationToken);
        if (manualChargeOverride is not null)
        {
            return await SaveDecisionAsync(
                decidedAtUtc,
                manualChargeOverride.ExpiresAtUtc,
                new CurrentBatteryDecision(
                    new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                    manualChargeOverride.TargetPowerWatts),
                batteryState,
                siteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                new[]
                {
                    new BatteryDecisionReason(
                        CurrentBatteryDecisionRuleIds.ManualGridCharge,
                        manualChargeOverride.Message)
                },
                cancellationToken);
        }

        var feedInCompensationPricePerKwh = await GetFeedInCompensationPricePerKwhAsync(cancellationToken);
        var gridPowerDeadbandWatts = await GetGridPowerDeadbandWattsAsync(cancellationToken);

        if (batteryState.MeasuredAtUtc < decidedAtUtc.Subtract(BatteryStateMaxAge))
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryState,
                siteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.StaleBatteryState,
                    "Die Live-SoC-Messung ist zu alt. Die Decision Engine bleibt deshalb im Idle-Zustand."),
                cancellationToken);
        }

        if (siteTelemetry.MeasuredAtUtc < decidedAtUtc.Subtract(SiteTelemetryMaxAge))
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryState,
                siteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.StaleSiteTelemetry,
                    "Die Live-Telemetrie fuer Netzbezug oder PV ist zu alt. Die Decision Engine bleibt deshalb im Idle-Zustand."),
                cancellationToken);
        }

        if (Math.Abs(siteTelemetry.CurrentGridImportWatts) > MaximumPlausiblePowerWatts || siteTelemetry.CurrentPvProductionWatts is < 0 or > MaximumPlausiblePowerWatts)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryState,
                siteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.InvalidSiteTelemetry,
                    "Die Live-Telemetrie enthaelt unplausible Leistungswerte. Die Decision Engine bleibt deshalb im Idle-Zustand."),
                cancellationToken);
        }

        var activePvChargePowerWatts = await GetActivePvChargePowerWattsAsync(decidedAtUtc, cancellationToken);
        var decisionSiteTelemetry = activePvChargePowerWatts > 0 && siteTelemetry.CurrentGridImportWatts < 0
            ? new CurrentSiteTelemetry(
                siteTelemetry.CurrentGridImportWatts - activePvChargePowerWatts,
                siteTelemetry.CurrentPvProductionWatts,
                siteTelemetry.MeasuredAtUtc,
                siteTelemetry.CurrentBatteryPowerWatts)
            : siteTelemetry;

        if (Math.Abs(decisionSiteTelemetry.CurrentGridImportWatts) <= gridPowerDeadbandWatts)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryState,
                decisionSiteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.GridPowerDeadband,
                    $"Die aktuelle Netzleistung von {decisionSiteTelemetry.CurrentGridImportWatts} Watt liegt innerhalb des konfigurierten Puffers von +/- {gridPowerDeadbandWatts} Watt. Die Decision Engine ignoriert diese kleine Abweichung und bleibt im Idle-Zustand."),
                cancellationToken);
        }

        IReadOnlyList<TibberPriceForecastSlot>? priceForecast = null;
        TibberPriceForecastSlot? currentPriceSlot = null;
        BatteryDecisionReason? missingCurrentPriceReason = null;

        try
        {
            priceForecast = await dependencies.TibberPriceForecastProvider.GetPriceForecastAsync(
                decidedAtUtc,
                decidedAtUtc.Add(DecisionLookahead),
                cancellationToken);
            currentPriceSlot = priceForecast.SingleOrDefault(priceSlot =>
                priceSlot.TimeSlot.StartsAtUtc <= decidedAtUtc &&
                decidedAtUtc < priceSlot.TimeSlot.EndsAtUtc);
        }
        catch (Exception exception)
        {
            missingCurrentPriceReason = new BatteryDecisionReason(
                CurrentBatteryDecisionRuleIds.MissingCurrentPrice,
                $"Die aktuellen Tibber-Preise konnten nicht geladen werden ({exception.Message}). Die Decision Engine bleibt deshalb im Idle-Zustand.");
        }

        if (decisionSiteTelemetry.CurrentGridImportWatts < 0)
        {
            return await CreateExportAbsorptionDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                decisionSiteTelemetry,
                currentPriceSlot,
                priceForecast ?? Array.Empty<TibberPriceForecastSlot>(),
                feedInCompensationPricePerKwh,
                cancellationToken);
        }

        if (missingCurrentPriceReason is not null)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryState,
                decisionSiteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                missingCurrentPriceReason,
                cancellationToken);
        }

        if (priceForecast is null)
        {
            throw new InvalidOperationException("Die Tibber-Preise wurden nicht geladen.");
        }

        if (currentPriceSlot is null)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryState,
                decisionSiteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.MissingCurrentPrice,
                    "Für den aktuellen Zeitpunkt liegt kein gültiger Tibber-Preis vor. Die Decision Engine bleibt deshalb im Idle-Zustand."),
                cancellationToken);
        }

        var priceRuleResult = tibberPriceDecisionRule.Evaluate(decidedAtUtc, priceForecast, batteryState);

        return priceRuleResult.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => await CreateGridChargeDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                decisionSiteTelemetry,
                currentPriceSlot,
                feedInCompensationPricePerKwh,
                priceRuleResult.Reasons[0],
                cancellationToken),
            BatteryDecisionState.Discharge => await CreateDischargeDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                decisionSiteTelemetry,
                currentPriceSlot,
                priceRuleResult.Reasons[0],
                batteryConfiguration.MinimumStateOfChargePercent,
                cancellationToken),
            _ => await TryCreateForecastBackedDischargeDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                decisionSiteTelemetry,
                currentPriceSlot,
                cancellationToken) ?? await SaveDecisionAsync(
                decidedAtUtc,
                currentPriceSlot.TimeSlot.EndsAtUtc,
                new CurrentBatteryDecision(
                    new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                    targetPowerWatts: 0),
                batteryState,
                decisionSiteTelemetry,
                currentPriceSlot.TotalPricePerKwh,
                currentPriceSlot.Currency,
                priceRuleResult.Reasons,
                cancellationToken)
        };
    }

    private async Task<CurrentBatteryDecisionResult?> TryCreateForecastBackedDischargeDecisionAsync(
        DateTimeOffset decidedAtUtc,
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration,
        CurrentSiteTelemetry siteTelemetry,
        TibberPriceForecastSlot currentPriceSlot,
        CancellationToken cancellationToken)
    {
        if (dependencies.BatteryForecastService is null ||
            siteTelemetry.CurrentGridImportWatts <= 0)
        {
            return null;
        }

        BatteryForecastResult forecastResult;
        try
        {
            forecastResult = await dependencies.BatteryForecastService.CalculateForecastAsync(
                decidedAtUtc,
                decidedAtUtc.Add(DecisionLookahead),
                cancellationToken);
        }
        catch (Exception exception)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                currentPriceSlot.TimeSlot.EndsAtUtc,
                batteryState,
                siteTelemetry,
                currentPriceSlot.TotalPricePerKwh,
                currentPriceSlot.Currency,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.ForecastKeepsDischargeReserve,
                    $"Der 24h-Forecast konnte nicht berechnet werden ({exception.Message}). Die Decision Engine entlädt bei neutralem Preis deshalb nicht."),
                cancellationToken);
        }

        var futureEntries = forecastResult.Entries
            .Where(entry => entry.TimeSlot.EndsAtUtc > decidedAtUtc)
            .OrderBy(entry => entry.TimeSlot.StartsAtUtc)
            .ToArray();
        var finalStateOfChargePercent = futureEntries.Length == 0
            ? batteryState.StateOfChargePercent
            : futureEntries[^1].StateOfChargeAfterPercent;
        var reservePercent = Math.Max(
            batteryConfiguration.TargetEndStateOfChargePercent,
            batteryConfiguration.PlanningMinimumStateOfChargePercent);

        if (finalStateOfChargePercent <= reservePercent)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                currentPriceSlot.TimeSlot.EndsAtUtc,
                batteryState,
                siteTelemetry,
                currentPriceSlot.TotalPricePerKwh,
                currentPriceSlot.Currency,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.ForecastKeepsDischargeReserve,
                    $"Der 24h-Forecast endet bei {finalStateOfChargePercent:0.#} Prozent SoC und schützt damit die Reserve von {reservePercent:0.#} Prozent. Die Decision Engine entlädt bei neutralem Preis nicht."),
                cancellationToken);
        }

        var reason = new BatteryDecisionReason(
            CurrentBatteryDecisionRuleIds.ForecastAllowsLoadCoverageDischarge,
            $"Aktueller Netzbezug von {siteTelemetry.CurrentGridImportWatts} Watt wird aus dem Akku gedeckt. Der 24h-Forecast endet trotz Verbrauch, günstiger Ladefenster und PV-Prognose bei {finalStateOfChargePercent:0.#} Prozent SoC und bleibt damit über der Reserve von {reservePercent:0.#} Prozent.");

        return await CreateDischargeDecisionAsync(
            decidedAtUtc,
            batteryState,
            batteryConfiguration,
            siteTelemetry,
            currentPriceSlot,
            reason,
            reservePercent,
            cancellationToken);
    }

    private async Task<LiveBatteryStateReadResult> TryGetBatteryStateAsync(
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return new LiveBatteryStateReadResult
            {
                BatteryState = await dependencies.BatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken),
                IsSuccess = true
            };
        }
        catch (InvalidOperationException exception)
        {
            return new LiveBatteryStateReadResult
            {
                BatteryState = CreateFallbackBatteryState(decidedAtUtc),
                IsSuccess = false,
                Reason = new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.MissingBatteryState,
                    $"Es liegt noch kein verwendbarer Live-Akkuladestand vor ({exception.Message}). Die Decision Engine bleibt deshalb im Idle-Zustand.")
            };
        }
    }

    private async Task<LiveSiteTelemetryReadResult> TryGetSiteTelemetryAsync(
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return new LiveSiteTelemetryReadResult
            {
                SiteTelemetry = await dependencies.CurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken),
                IsSuccess = true
            };
        }
        catch (InvalidOperationException exception)
        {
            return new LiveSiteTelemetryReadResult
            {
                SiteTelemetry = CreateFallbackSiteTelemetry(decidedAtUtc),
                IsSuccess = false,
                Reason = new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.MissingSiteTelemetry,
                    $"Es liegen noch keine vollstaendigen Live-Telemetriedaten fuer Netzbezug, Hausverbrauch oder PV vor ({exception.Message}). Die Decision Engine bleibt deshalb im Idle-Zustand.")
            };
        }
    }

    private async Task<int> GetActivePvChargePowerWattsAsync(
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken)
    {
        var recentDecisions = await dependencies.DecisionLogRepository.GetRecentDecisionsAsync(1, cancellationToken);
        var latestDecision = recentDecisions.FirstOrDefault();

        if (latestDecision is null ||
            latestDecision.ValidFromUtc > decidedAtUtc ||
            latestDecision.ValidToUtc <= decidedAtUtc ||
            latestDecision.Decision.Instruction.DecisionState != BatteryDecisionState.Charge ||
            latestDecision.Decision.Instruction.ChargeSource != BatteryChargeSource.PV)
        {
            return 0;
        }

        return Math.Max(0, latestDecision.Decision.TargetPowerWatts);
    }

    private async Task<ManualChargeOverride?> TryGetManualChargeOverrideAsync(
        DateTimeOffset decidedAtUtc,
        BatteryConfiguration batteryConfiguration,
        CancellationToken cancellationToken)
    {
        var powerSetting = await dependencies.ControllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.ManualChargePowerWattsKey,
            cancellationToken);
        var expiresAtSetting = await dependencies.ControllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.ManualChargeExpiresAtUtcKey,
            cancellationToken);

        if (powerSetting?.Value is null ||
            expiresAtSetting?.Value is null ||
            !int.TryParse(powerSetting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedPowerWatts) ||
            requestedPowerWatts <= 0 ||
            !DateTimeOffset.TryParse(
                expiresAtSetting.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiresAtUtc) ||
            expiresAtUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc <= decidedAtUtc)
        {
            return null;
        }

        var targetPowerWatts = LimitChargePowerBySettings(requestedPowerWatts, batteryConfiguration);
        if (targetPowerWatts <= 0)
        {
            return null;
        }

        var message = requestedPowerWatts == targetPowerWatts
            ? $"Manuelle Netzladung mit {targetPowerWatts} Watt ist bis {expiresAtUtc:O} aktiv."
            : $"Manuelle Netzladung würde {requestedPowerWatts} Watt anfordern. Laut Einstellungen sind maximal {targetPowerWatts} Watt möglich; die Begrenzung gilt bis {expiresAtUtc:O}.";

        return new ManualChargeOverride(targetPowerWatts, expiresAtUtc, message);
    }

    private async Task<CurrentBatteryDecisionResult> CreateExportAbsorptionDecisionAsync(
        DateTimeOffset decidedAtUtc,
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration,
        CurrentSiteTelemetry siteTelemetry,
        TibberPriceForecastSlot? currentPriceSlot,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal feedInCompensationPricePerKwh,
        CancellationToken cancellationToken)
    {
        var targetPowerWatts = gridExportAbsorptionPolicy.CalculateTargetPowerWatts(
            siteTelemetry.CurrentGridImportWatts,
            batteryState,
            batteryConfiguration,
            priceForecast,
            feedInCompensationPricePerKwh);

        if (targetPowerWatts <= 0)
        {
            var reason = batteryState.StateOfChargePercent >= batteryConfiguration.PlanningMaximumStateOfChargePercent
                ? new BatteryDecisionReason(
                    BatteryForecastRuleIds.PlanningMaximumSocHeadroom,
                    $"Das Planungs-Maximum von {batteryConfiguration.PlanningMaximumStateOfChargePercent:0.#} Prozent ist erreicht. Die Decision Engine nimmt aktuellen Netzexport deshalb nicht weiter in die Batterie auf.")
                : new BatteryDecisionReason(
                    BatteryForecastRuleIds.PreserveHeadroomForNegativePrice,
                    "Die Decision Engine haelt Kapazitaet fuer spaetere negative Tibber-Preise frei und bleibt deshalb trotz aktuellem Netzexport im Idle-Zustand.");

            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                currentPriceSlot?.TimeSlot.EndsAtUtc ?? decidedAtUtc.AddMinutes(15),
                batteryState,
                siteTelemetry,
                currentPriceSlot?.TotalPricePerKwh,
                currentPriceSlot?.Currency,
                reason,
                cancellationToken);
        }

        var reasons = new List<BatteryDecisionReason>
        {
            new(
                CurrentBatteryDecisionRuleIds.AbsorbGridExport,
                $"Aktueller Netzexport von {Math.Abs(siteTelemetry.CurrentGridImportWatts)} Watt wird in die Batterie geladen, damit keine Einspeisung stehen bleibt.")
        };
        AddChargePowerLimitReason(
            reasons,
            calculatedTargetPowerWatts: Math.Abs(siteTelemetry.CurrentGridImportWatts),
            limitedTargetPowerWatts: targetPowerWatts);

        return await SaveDecisionAsync(
            decidedAtUtc,
            currentPriceSlot?.TimeSlot.EndsAtUtc ?? decidedAtUtc.AddMinutes(15),
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
                targetPowerWatts),
            batteryState,
            siteTelemetry,
            currentPriceSlot?.TotalPricePerKwh,
            currentPriceSlot?.Currency,
            reasons,
            cancellationToken);
    }

    private async Task<CurrentBatteryDecisionResult> CreateGridChargeDecisionAsync(
        DateTimeOffset decidedAtUtc,
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration,
        CurrentSiteTelemetry siteTelemetry,
        TibberPriceForecastSlot currentPriceSlot,
        decimal feedInCompensationPricePerKwh,
        BatteryDecisionReason baseReason,
        CancellationToken cancellationToken)
    {
        var targetStateOfChargePercent = currentPriceSlot.TotalPricePerKwh < feedInCompensationPricePerKwh
            ? 100m
            : batteryConfiguration.PlanningMaximumStateOfChargePercent;
        var currentBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * batteryState.StateOfChargePercent / 100m;
        var targetBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * targetStateOfChargePercent / 100m;
        var availableStoredCapacityKwh = targetBatteryEnergyKwh - currentBatteryEnergyKwh;
        var singleDirectionEfficiency = CalculateSingleDirectionEfficiency(batteryConfiguration);
        var calculatedChargeInputEnergyKwh = Math.Max(0m, availableStoredCapacityKwh / singleDirectionEfficiency);
        var calculatedTargetPowerWatts = CalculatePowerWatts(calculatedChargeInputEnergyKwh, currentPriceSlot.TimeSlot.Duration);
        var targetPowerWatts = LimitChargePowerBySettings(calculatedTargetPowerWatts, batteryConfiguration);

        if (targetPowerWatts <= 0)
        {
            var reason = targetStateOfChargePercent < 100m
                ? new BatteryDecisionReason(
                    BatteryForecastRuleIds.PlanningMaximumSocHeadroom,
                    "Das Planungs-Maximum ist bereits erreicht. Die Decision Engine haelt deshalb PV-Puffer frei und bleibt im Idle-Zustand.")
                : new BatteryDecisionReason(CurrentBatteryDecisionRuleIds.BatteryFull, "Der Akku ist bereits voll. Die Decision Engine bleibt deshalb im Idle-Zustand.");

            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                currentPriceSlot.TimeSlot.EndsAtUtc,
                batteryState,
                siteTelemetry,
                currentPriceSlot.TotalPricePerKwh,
                currentPriceSlot.Currency,
                reason,
                cancellationToken);
        }

        var ruleId = targetStateOfChargePercent < 100m && targetPowerWatts < batteryConfiguration.MaximumChargePowerWatts
            ? BatteryForecastRuleIds.PlanningMaximumGridChargeLimit
            : baseReason.RuleName;
        var reasonMessage = ruleId == BatteryForecastRuleIds.PlanningMaximumGridChargeLimit
            ? $"{baseReason.Message} Das Planungs-Maximum begrenzt die Netzladung auf {targetStateOfChargePercent:0.#} Prozent, damit PV-Puffer frei bleibt."
            : baseReason.Message;

        var reasons = new List<BatteryDecisionReason> { new(ruleId, reasonMessage) };
        AddChargePowerLimitReason(reasons, calculatedTargetPowerWatts, targetPowerWatts);

        return await SaveDecisionAsync(
            decidedAtUtc,
            currentPriceSlot.TimeSlot.EndsAtUtc,
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                targetPowerWatts),
            batteryState,
            siteTelemetry,
            currentPriceSlot.TotalPricePerKwh,
            currentPriceSlot.Currency,
            reasons,
            cancellationToken);
    }

    private async Task<CurrentBatteryDecisionResult> CreateDischargeDecisionAsync(
        DateTimeOffset decidedAtUtc,
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration,
        CurrentSiteTelemetry siteTelemetry,
        TibberPriceForecastSlot currentPriceSlot,
        BatteryDecisionReason baseReason,
        decimal reserveStateOfChargePercent,
        CancellationToken cancellationToken)
    {
        var powerLimitedTargetWatts = dischargePowerLimiter.CalculateTargetPowerWatts(
            siteTelemetry.CurrentGridImportWatts,
            batteryConfiguration);

        if (powerLimitedTargetWatts <= 0)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                currentPriceSlot.TimeSlot.EndsAtUtc,
                batteryState,
                siteTelemetry,
                currentPriceSlot.TotalPricePerKwh,
                currentPriceSlot.Currency,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.NoGridImportForDischarge,
                    "Ohne aktuellen Netzbezug wird nicht entladen, damit keine Einspeisung ins Netz entsteht."),
                cancellationToken);
        }

        var currentBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * batteryState.StateOfChargePercent / 100m;
        var minimumBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * reserveStateOfChargePercent / 100m;
        var singleDirectionEfficiency = CalculateSingleDirectionEfficiency(batteryConfiguration);
        var availableLoadCoverageKwh = Math.Max(0m, (currentBatteryEnergyKwh - minimumBatteryEnergyKwh) * singleDirectionEfficiency);
        var maximumEnergyLimitedWatts = CalculatePowerWatts(availableLoadCoverageKwh, currentPriceSlot.TimeSlot.Duration);
        var targetPowerWatts = Math.Min(powerLimitedTargetWatts, maximumEnergyLimitedWatts);

        if (targetPowerWatts <= 0)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                currentPriceSlot.TimeSlot.EndsAtUtc,
                batteryState,
                siteTelemetry,
                currentPriceSlot.TotalPricePerKwh,
                currentPriceSlot.Currency,
                new BatteryDecisionReason(
                    BatteryForecastRuleIds.MinimumSocReserve,
                    $"Die Entlade-Reserve von {reserveStateOfChargePercent:0.#} Prozent ist erreicht. Die Decision Engine entlädt deshalb nicht weiter."),
                cancellationToken);
        }

        return await SaveDecisionAsync(
            decidedAtUtc,
            currentPriceSlot.TimeSlot.EndsAtUtc,
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
                targetPowerWatts),
            batteryState,
            siteTelemetry,
            currentPriceSlot.TotalPricePerKwh,
            currentPriceSlot.Currency,
            new[] { baseReason },
            cancellationToken);
    }

    private async Task<CurrentBatteryDecisionResult> SaveIdleDecisionAsync(
        DateTimeOffset decidedAtUtc,
        DateTimeOffset validToUtc,
        BatteryState batteryState,
        CurrentSiteTelemetry siteTelemetry,
        decimal? tibberPricePerKwh,
        string? tibberPriceCurrency,
        BatteryDecisionReason reason,
        CancellationToken cancellationToken)
    {
        return await SaveDecisionAsync(
            decidedAtUtc,
            validToUtc,
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                targetPowerWatts: 0),
            batteryState,
            siteTelemetry,
            tibberPricePerKwh,
            tibberPriceCurrency,
            new[] { reason },
            cancellationToken);
    }

    private async Task<CurrentBatteryDecisionResult> SaveDecisionAsync(
        DateTimeOffset decidedAtUtc,
        DateTimeOffset validToUtc,
        CurrentBatteryDecision decision,
        BatteryState batteryState,
        CurrentSiteTelemetry siteTelemetry,
        decimal? tibberPricePerKwh,
        string? tibberPriceCurrency,
        IReadOnlyList<BatteryDecisionReason> reasons,
        CancellationToken cancellationToken)
    {
        var inputSummaryJson = JsonSerializer.Serialize(new
        {
            BatteryStateOfChargePercent = batteryState.StateOfChargePercent,
            BatteryMeasuredAtUtc = batteryState.MeasuredAtUtc,
            CurrentGridImportWatts = siteTelemetry.CurrentGridImportWatts,
            CurrentPvProductionWatts = siteTelemetry.CurrentPvProductionWatts,
            CurrentBatteryPowerWatts = siteTelemetry.CurrentBatteryPowerWatts,
            SiteTelemetryMeasuredAtUtc = siteTelemetry.MeasuredAtUtc,
            TibberPricePerKwh = tibberPricePerKwh,
            TibberPriceCurrency = tibberPriceCurrency
        });

        var result = new CurrentBatteryDecisionResult(
            decidedAtUtc,
            decidedAtUtc,
            validToUtc,
            decision,
            batteryState,
            siteTelemetry,
            tibberPricePerKwh,
            tibberPriceCurrency,
            reasons,
            inputSummaryJson);

        await dependencies.DecisionLogRepository.SaveDecisionAsync(
            new DecisionLogEntry(
                Guid.NewGuid(),
                decidedAtUtc,
                decidedAtUtc,
                validToUtc,
                decision,
                batteryState.StateOfChargePercent,
                tibberPricePerKwh,
                tibberPriceCurrency,
                Math.Max(0, siteTelemetry.CurrentGridImportWatts),
                Math.Max(0, -siteTelemetry.CurrentGridImportWatts),
                inputSummaryJson,
                reasons),
            cancellationToken);

        return result;
    }

    private async Task<decimal> GetFeedInCompensationPricePerKwhAsync(CancellationToken cancellationToken)
    {
        var setting = await dependencies.ControllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey,
            cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException("Die Einspeiseverguetung ist nicht konfiguriert.");
        }

        if (!decimal.TryParse(setting.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var feedInCompensationPricePerKwh))
        {
            throw new InvalidOperationException("Die Einspeiseverguetung muss als Dezimalzahl konfiguriert sein.");
        }

        if (feedInCompensationPricePerKwh < 0m)
        {
            throw new InvalidOperationException("Die Einspeiseverguetung darf nicht negativ sein.");
        }

        return feedInCompensationPricePerKwh;
    }

    private async Task<int> GetGridPowerDeadbandWattsAsync(CancellationToken cancellationToken)
    {
        var setting = await dependencies.ControllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.TelemetryGridPowerDeadbandWattsKey,
            cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException("Der Netzleistungs-Puffer ist nicht konfiguriert.");
        }

        if (!int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deadbandWatts))
        {
            throw new InvalidOperationException("Der Netzleistungs-Puffer muss als ganze Watt-Zahl konfiguriert sein.");
        }

        if (deadbandWatts < 0)
        {
            throw new InvalidOperationException("Der Netzleistungs-Puffer darf nicht negativ sein.");
        }

        return deadbandWatts;
    }

    private static decimal CalculateSingleDirectionEfficiency(BatteryConfiguration batteryConfiguration)
    {
        return (decimal)Math.Sqrt((double)(batteryConfiguration.RoundTripEfficiencyPercent / 100m));
    }

    private static int CalculatePowerWatts(decimal energyKwh, TimeSpan duration)
    {
        if (energyKwh <= 0m || duration <= TimeSpan.Zero)
        {
            return 0;
        }

        return (int)Math.Round(energyKwh / (decimal)duration.TotalHours * 1000m, MidpointRounding.AwayFromZero);
    }

    private static int LimitChargePowerBySettings(int calculatedTargetPowerWatts, BatteryConfiguration batteryConfiguration)
    {
        return Math.Min(calculatedTargetPowerWatts, batteryConfiguration.MaximumChargePowerWatts);
    }

    private static void AddChargePowerLimitReason(
        ICollection<BatteryDecisionReason> reasons,
        int calculatedTargetPowerWatts,
        int limitedTargetPowerWatts)
    {
        if (calculatedTargetPowerWatts <= limitedTargetPowerWatts)
        {
            return;
        }

        reasons.Add(new BatteryDecisionReason(
            CurrentBatteryDecisionRuleIds.ChargePowerLimitedBySettings,
            $"Errechnet waren {calculatedTargetPowerWatts} Watt Ladeleistung. Laut Einstellungen sind maximal {limitedTargetPowerWatts} Watt möglich."));
    }

    private static BatteryState CreateFallbackBatteryState(DateTimeOffset decidedAtUtc)
    {
        return new BatteryState(0m, decidedAtUtc);
    }

    private static CurrentSiteTelemetry CreateFallbackSiteTelemetry(DateTimeOffset decidedAtUtc)
    {
        return new CurrentSiteTelemetry(0, 0, decidedAtUtc);
    }

    private sealed class LiveBatteryStateReadResult
    {
        public required BatteryState BatteryState { get; init; }

        public required bool IsSuccess { get; init; }

        public BatteryDecisionReason? Reason { get; init; }
    }

    private sealed class LiveSiteTelemetryReadResult
    {
        public required CurrentSiteTelemetry SiteTelemetry { get; init; }

        public required bool IsSuccess { get; init; }

        public BatteryDecisionReason? Reason { get; init; }
    }

    private sealed record ManualChargeOverride(
        int TargetPowerWatts,
        DateTimeOffset ExpiresAtUtc,
        string Message);
}
