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
        var batteryState = await dependencies.BatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        var batteryConfiguration = await dependencies.BatteryConfigurationProvider.GetBatteryConfigurationAsync(cancellationToken);
        var siteTelemetry = await dependencies.CurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken);
        var feedInCompensationPricePerKwh = await GetFeedInCompensationPricePerKwhAsync(cancellationToken);
        var priceForecast = await dependencies.TibberPriceForecastProvider.GetPriceForecastAsync(
            decidedAtUtc,
            decidedAtUtc.Add(DecisionLookahead),
            cancellationToken);
        var currentPriceSlot = priceForecast.SingleOrDefault(priceSlot =>
            priceSlot.TimeSlot.StartsAtUtc <= decidedAtUtc &&
            decidedAtUtc < priceSlot.TimeSlot.EndsAtUtc);

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

        if (currentPriceSlot is null)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryState,
                siteTelemetry,
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.MissingCurrentPrice,
                    "Fuer den aktuellen Zeitpunkt liegt kein gueltiger Tibber-Preis vor. Die Decision Engine bleibt deshalb im Idle-Zustand."),
                cancellationToken);
        }

        if (siteTelemetry.CurrentGridImportWatts < 0)
        {
            return await CreateExportAbsorptionDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                siteTelemetry,
                currentPriceSlot,
                priceForecast,
                feedInCompensationPricePerKwh,
                cancellationToken);
        }

        var priceRuleResult = tibberPriceDecisionRule.Evaluate(decidedAtUtc, priceForecast, batteryState);

        return priceRuleResult.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => await CreateGridChargeDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                siteTelemetry,
                currentPriceSlot,
                feedInCompensationPricePerKwh,
                priceRuleResult.Reasons[0],
                cancellationToken),
            BatteryDecisionState.Discharge => await CreateDischargeDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                siteTelemetry,
                currentPriceSlot,
                priceRuleResult.Reasons[0],
                cancellationToken),
            _ => await SaveDecisionAsync(
                decidedAtUtc,
                currentPriceSlot.TimeSlot.EndsAtUtc,
                new CurrentBatteryDecision(
                    new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                    targetPowerWatts: 0),
                batteryState,
                siteTelemetry,
                currentPriceSlot.TotalPricePerKwh,
                currentPriceSlot.Currency,
                priceRuleResult.Reasons,
                cancellationToken)
        };
    }

    private async Task<CurrentBatteryDecisionResult> CreateExportAbsorptionDecisionAsync(
        DateTimeOffset decidedAtUtc,
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration,
        CurrentSiteTelemetry siteTelemetry,
        TibberPriceForecastSlot currentPriceSlot,
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
            var reason = batteryState.StateOfChargePercent >= 100m
                ? new BatteryDecisionReason(CurrentBatteryDecisionRuleIds.BatteryFull, "Der Akku ist bereits voll. Die Decision Engine kann aktuellen Netzexport deshalb nicht weiter aufnehmen.")
                : new BatteryDecisionReason(
                    BatteryForecastRuleIds.PreserveHeadroomForNegativePrice,
                    "Die Decision Engine haelt Kapazitaet fuer spaetere negative Tibber-Preise frei und bleibt deshalb trotz aktuellem Netzexport im Idle-Zustand.");

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

        return await SaveDecisionAsync(
            decidedAtUtc,
            currentPriceSlot.TimeSlot.EndsAtUtc,
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
                targetPowerWatts),
            batteryState,
            siteTelemetry,
            currentPriceSlot.TotalPricePerKwh,
            currentPriceSlot.Currency,
            new[]
            {
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.AbsorbGridExport,
                    $"Aktueller Netzexport von {Math.Abs(siteTelemetry.CurrentGridImportWatts)} Watt wird in die Batterie geladen, damit keine Einspeisung stehen bleibt.")
            },
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
        var maximumChargeEnergyKwh = batteryConfiguration.MaximumChargePowerWatts / 1000m * (decimal)currentPriceSlot.TimeSlot.Duration.TotalHours;
        var chargeInputEnergyKwh = Math.Max(0m, Math.Min(maximumChargeEnergyKwh, availableStoredCapacityKwh / singleDirectionEfficiency));
        var targetPowerWatts = CalculatePowerWatts(chargeInputEnergyKwh, currentPriceSlot.TimeSlot.Duration);

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
            new[] { new BatteryDecisionReason(ruleId, reasonMessage) },
            cancellationToken);
    }

    private async Task<CurrentBatteryDecisionResult> CreateDischargeDecisionAsync(
        DateTimeOffset decidedAtUtc,
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration,
        CurrentSiteTelemetry siteTelemetry,
        TibberPriceForecastSlot currentPriceSlot,
        BatteryDecisionReason baseReason,
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
        var minimumBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * batteryConfiguration.MinimumStateOfChargePercent / 100m;
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
                    "Der minimale Akkuladestand ist erreicht. Die Decision Engine entlaedt deshalb nicht weiter."),
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
}
