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
    private static readonly TimeSpan SiteTelemetryMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DecisionLookahead = TimeSpan.FromHours(24);
    private static readonly TimeSpan ActiveDischargeTargetMaxAge = TimeSpan.FromMinutes(2);
    private const int MaximumPlausiblePowerWatts = 100_000;

    private readonly CurrentBatteryDecisionServiceDependencies dependencies;
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

        // Without a measured battery power the previous PV charge target is the only hint how much export the battery already absorbs.
        var decisionSiteTelemetry = siteTelemetry.CurrentBatteryPowerWatts is null
            ? await CreateFallbackDecisionSiteTelemetryAsync(decidedAtUtc, siteTelemetry, cancellationToken)
            : siteTelemetry;
        var activeDischargeTargetWatts = await GetActiveDischargeTargetWattsAsync(decidedAtUtc, cancellationToken);
        var gridPowerWithoutBatteryWatts = CalculateGridPowerWithoutBatteryWatts(decisionSiteTelemetry, batteryConfiguration, activeDischargeTargetWatts);

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
                $"Die aktuellen Strompreise konnten nicht geladen werden ({exception.Message}). Die Decision Engine bleibt deshalb im Idle-Zustand.");
        }

        if (Math.Abs(gridPowerWithoutBatteryWatts) <= gridPowerDeadbandWatts)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                decidedAtUtc.AddMinutes(15),
                batteryState,
                decisionSiteTelemetry,
                tibberPricePerKwh: currentPriceSlot?.TotalPricePerKwh,
                tibberPriceCurrency: currentPriceSlot?.Currency,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.GridPowerDeadband,
                    $"Die aktuelle Netzleistung ohne Akku von {gridPowerWithoutBatteryWatts} Watt liegt innerhalb des konfigurierten Puffers von +/- {gridPowerDeadbandWatts} Watt. Die Decision Engine ignoriert diese kleine Abweichung und bleibt im Idle-Zustand."),
                cancellationToken);
        }

        if (gridPowerWithoutBatteryWatts < 0)
        {
            // Export caused by the discharging E3/DC battery is no PV surplus; only the remaining export may be absorbed.
            var absorbableGridPowerWatts = Math.Min(0, gridPowerWithoutBatteryWatts + decisionSiteTelemetry.SupplementaryBatteryDischargeWatts);

            // A cheap PV slot can also be the planned grid charging slot: absorb the surplus and add the planned grid share.
            if (currentPriceSlot is not null)
            {
                var plannedExportSlot = await TryGetPlannedForecastSlotAsync(decidedAtUtc, currentPriceSlot, currentNetLoadWatts: null, cancellationToken);
                if (plannedExportSlot.Entry is { Decision.Instruction: { DecisionState: BatteryDecisionState.Charge, ChargeSource: BatteryChargeSource.Grid } } plannedEntry)
                {
                    var slotHours = (decimal)plannedEntry.TimeSlot.Duration.TotalHours;
                    var forecastPvSurplusWatts = slotHours <= 0m
                        ? 0
                        : (int)Math.Round(Math.Max(0m, -plannedEntry.ExpectedGridImportBeforeBatteryKwh) / slotHours * 1000m, MidpointRounding.AwayFromZero);
                    var plannedGridWatts = Math.Max(0, plannedEntry.Decision.TargetPowerWatts - forecastPvSurplusWatts);

                    return await CreateGridChargeDecisionAsync(
                        decidedAtUtc,
                        batteryState,
                        batteryConfiguration,
                        decisionSiteTelemetry,
                        currentPriceSlot,
                        feedInCompensationPricePerKwh,
                        plannedEntry.Reasons[0],
                        -absorbableGridPowerWatts + plannedGridWatts,
                        cancellationToken);
                }
            }

            if (-absorbableGridPowerWatts <= gridPowerDeadbandWatts)
            {
                return await SaveIdleDecisionAsync(
                    decidedAtUtc,
                    decidedAtUtc.AddMinutes(15),
                    batteryState,
                    decisionSiteTelemetry,
                    currentPriceSlot?.TotalPricePerKwh,
                    currentPriceSlot?.Currency,
                    new BatteryDecisionReason(
                        CurrentBatteryDecisionRuleIds.SupplementaryDischargeExport,
                        $"Die Einspeisung von {-gridPowerWithoutBatteryWatts} Watt stammt aus dem entladenden Zweitspeicher ({decisionSiteTelemetry.SupplementaryBatteryDischargeWatts} Watt). Sie wird nicht in den Akku geladen, damit keine Energie zwischen den Speichern verschoben wird."),
                    cancellationToken);
            }

            return await CreateExportAbsorptionDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                decisionSiteTelemetry,
                absorbableGridPowerWatts,
                currentPriceSlot,
                priceForecast ?? Array.Empty<TibberPriceForecastSlot>(),
                feedInCompensationPricePerKwh,
                cancellationToken);
        }

        var dischargeDemandWatts = CalculateDischargeDemandWatts(gridPowerWithoutBatteryWatts, decisionSiteTelemetry, gridPowerDeadbandWatts);

        if (missingCurrentPriceReason is not null)
        {
            return await CreateDischargeDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                decisionSiteTelemetry,
                dischargeDemandWatts,
                currentPriceSlot: null,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.CoverCurrentGridImport,
                    $"Aktueller Verbrauch ohne Akku von {gridPowerWithoutBatteryWatts} Watt wird aus dem Akku gedeckt. Der Strompreis ist in diesem Zyklus nicht verfuegbar; die Decision Engine startet deshalb keine preisbasierte Netzladung."),
                batteryConfiguration.MinimumStateOfChargePercent,
                cancellationToken);
        }

        if (priceForecast is null)
        {
            throw new InvalidOperationException("Die Strompreise wurden nicht geladen.");
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
                    "Für den aktuellen Zeitpunkt liegt kein gültiger Strompreis vor. Die Decision Engine bleibt deshalb im Idle-Zustand."),
                cancellationToken);
        }

        // The forecast plan decides grid charging and holding, so direct decision and forecast chart follow the same rules.
        // The current slot is planned with the live load: an unplanned load such as an electric car is only covered when
        // the battery energy is not worth more in a later, more expensive slot.
        var plannedSlot = await TryGetPlannedForecastSlotAsync(
            decidedAtUtc,
            currentPriceSlot,
            Math.Max(0, gridPowerWithoutBatteryWatts - decisionSiteTelemetry.SupplementaryBatteryChargeFromSiteWatts),
            cancellationToken);
        var planEntry = plannedSlot.Entry;

        if (planEntry?.Decision.Instruction is { DecisionState: BatteryDecisionState.Charge, ChargeSource: BatteryChargeSource.Grid })
        {
            return await CreateGridChargeDecisionAsync(
                decidedAtUtc,
                batteryState,
                batteryConfiguration,
                decisionSiteTelemetry,
                currentPriceSlot,
                feedInCompensationPricePerKwh,
                planEntry.Reasons[0],
                planEntry.Decision.TargetPowerWatts,
                cancellationToken);
        }

        if (planEntry is not null && IsPlannedBatteryHold(planEntry))
        {
            return await SaveDecisionAsync(
                decidedAtUtc,
                currentPriceSlot.TimeSlot.EndsAtUtc,
                new CurrentBatteryDecision(
                    new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                    targetPowerWatts: 0),
                batteryState,
                decisionSiteTelemetry,
                currentPriceSlot.TotalPricePerKwh,
                currentPriceSlot.Currency,
                planEntry.Reasons,
                cancellationToken);
        }

        // A partial hold caps the discharge at the planned power; otherwise the battery covers the live demand completely.
        var isPartialHold = planEntry?.Decision.Instruction.DecisionState == BatteryDecisionState.Discharge &&
            planEntry.Reasons[0].RuleName is BatteryForecastRuleIds.HoldForExpensiveSlot or BatteryForecastRuleIds.EndSocReserve;
        var effectiveDischargeDemandWatts = isPartialHold
            ? Math.Min(dischargeDemandWatts, planEntry!.Decision.TargetPowerWatts)
            : dischargeDemandWatts;
        var dischargeReason = isPartialHold
            ? planEntry!.Reasons[0]
            : new BatteryDecisionReason(
                plannedSlot.FailureMessage is null && planEntry is not null
                    ? CurrentBatteryDecisionRuleIds.ForecastAllowsLoadCoverageDischarge
                    : CurrentBatteryDecisionRuleIds.CoverCurrentGridImport,
                $"Aktueller Verbrauch ohne Akku von {gridPowerWithoutBatteryWatts} Watt wird aus dem Akku gedeckt. {plannedSlot.FailureMessage ?? "Der Forecast plant fuer diesen Slot weder Netzladung noch das Zurueckhalten von Akkuenergie."}{CreateSupplementaryChargeNote(decisionSiteTelemetry)}");

        return await CreateDischargeDecisionAsync(
            decidedAtUtc,
            batteryState,
            batteryConfiguration,
            decisionSiteTelemetry,
            effectiveDischargeDemandWatts,
            currentPriceSlot,
            dischargeReason,
            batteryConfiguration.PlanningMinimumStateOfChargePercent,
            cancellationToken);
    }

    private async Task<PlannedForecastSlot> TryGetPlannedForecastSlotAsync(
        DateTimeOffset decidedAtUtc,
        TibberPriceForecastSlot currentPriceSlot,
        int? currentNetLoadWatts,
        CancellationToken cancellationToken)
    {
        if (dependencies.BatteryForecastService is null)
        {
            return new PlannedForecastSlot(null, "Es ist kein Forecast verfuegbar; die Decision Engine startet deshalb keine Netzladung.");
        }

        try
        {
            // The forecast starts at the current slot so the plan for "now" is part of the result.
            var forecastResult = await dependencies.BatteryForecastService.CalculateForecastAsync(
                new BatteryForecastRequest
                {
                    StartsAtUtc = currentPriceSlot.TimeSlot.StartsAtUtc,
                    EndsAtUtc = decidedAtUtc.Add(DecisionLookahead),
                    CurrentNetLoadWatts = currentNetLoadWatts
                },
                cancellationToken);
            var entry = forecastResult.Entries.FirstOrDefault(forecastEntry =>
                forecastEntry.TimeSlot.StartsAtUtc <= decidedAtUtc &&
                decidedAtUtc < forecastEntry.TimeSlot.EndsAtUtc);

            return entry is null
                ? new PlannedForecastSlot(null, "Der Forecast enthaelt keinen Eintrag fuer den aktuellen Slot; die Decision Engine startet deshalb keine Netzladung.")
                : new PlannedForecastSlot(entry, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PlannedForecastSlot(null, $"Der Forecast konnte nicht berechnet werden ({exception.Message}); die Decision Engine startet deshalb keine Netzladung.");
        }
    }

    private static bool IsPlannedBatteryHold(BatteryForecastEntry planEntry)
    {
        // These forecast rules intentionally keep battery energy or buy grid energy at a negative price instead of discharging.
        return planEntry.Decision.Instruction.DecisionState == BatteryDecisionState.Idle &&
            (planEntry.TibberPricePerKwh < 0m ||
                planEntry.Reasons[0].RuleName is BatteryForecastRuleIds.HoldForExpensiveSlot
                    or BatteryForecastRuleIds.EndSocReserve
                    or BatteryForecastRuleIds.WaitForNegativePriceWindow);
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

    private async Task<CurrentSiteTelemetry> CreateFallbackDecisionSiteTelemetryAsync(
        DateTimeOffset decidedAtUtc,
        CurrentSiteTelemetry siteTelemetry,
        CancellationToken cancellationToken)
    {
        var activePvChargePowerWatts = await GetActivePvChargePowerWattsAsync(decidedAtUtc, cancellationToken);

        return activePvChargePowerWatts > 0 && siteTelemetry.CurrentGridImportWatts < 0
            ? new CurrentSiteTelemetry(
                siteTelemetry.CurrentGridImportWatts - activePvChargePowerWatts,
                siteTelemetry.CurrentPvProductionWatts,
                siteTelemetry.MeasuredAtUtc,
                siteTelemetry.CurrentBatteryPowerWatts)
            {
                SupplementaryBatteryChargeFromSiteWatts = siteTelemetry.SupplementaryBatteryChargeFromSiteWatts,
                SupplementaryBatteryDischargeWatts = siteTelemetry.SupplementaryBatteryDischargeWatts
            }
            : siteTelemetry;
    }

    /// <summary>
    /// Calculates the site grid power the battery has to compensate, i.e. the measured grid power without the current battery action.
    /// </summary>
    /// <remarks>
    /// The measured grid power already contains the running battery action. Using it directly as the next target lets the battery
    /// cover only the residual import it leaves behind, so load coverage collapses to roughly half and charge/discharge oscillates.
    /// The battery power is measured on the DC side, so it is converted to the AC side with the configured single-direction efficiency.
    /// While discharging, the DC power also feeds loads on the inverter AC output and contains losses that grow with power; both are
    /// invisible at the grid meter. The previously requested discharge power is what the inverter actually delivers towards the grid
    /// connection, so the smaller of both values is used. This closes the loop on the measured grid power and prevents feed-in.
    /// </remarks>
    private static int CalculateGridPowerWithoutBatteryWatts(
        CurrentSiteTelemetry siteTelemetry,
        BatteryConfiguration batteryConfiguration,
        int? activeDischargeTargetWatts)
    {
        if (siteTelemetry.CurrentBatteryPowerWatts is not { } batteryDcPowerWatts)
        {
            return siteTelemetry.CurrentGridImportWatts;
        }

        var singleDirectionEfficiency = CalculateSingleDirectionEfficiency(batteryConfiguration);
        var batteryAcPowerWatts = batteryDcPowerWatts >= 0
            ? batteryDcPowerWatts / singleDirectionEfficiency
            : batteryDcPowerWatts * singleDirectionEfficiency;

        if (batteryAcPowerWatts < 0m && activeDischargeTargetWatts is { } requestedDischargeWatts)
        {
            batteryAcPowerWatts = Math.Max(batteryAcPowerWatts, -requestedDischargeWatts);
        }

        return siteTelemetry.CurrentGridImportWatts - (int)Math.Round(batteryAcPowerWatts, MidpointRounding.AwayFromZero);
    }

    private static string CreateSupplementaryChargeNote(CurrentSiteTelemetry siteTelemetry)
    {
        return siteTelemetry.SupplementaryBatteryChargeFromSiteWatts > 0
            ? $" Der Zweitspeicher laedt gerade mit {siteTelemetry.SupplementaryBatteryChargeFromSiteWatts} Watt aus dem Hausnetz; dieser Anteil wird nicht aus dem Akku gedeckt."
            : string.Empty;
    }

    private async Task<int?> GetActiveDischargeTargetWattsAsync(
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken)
    {
        var recentDecisions = await dependencies.DecisionLogRepository.GetRecentDecisionsAsync(1, cancellationToken);
        var latestDecision = recentDecisions.FirstOrDefault();

        return latestDecision is not null &&
            latestDecision.ValidFromUtc <= decidedAtUtc &&
            decidedAtUtc < latestDecision.ValidToUtc &&
            decidedAtUtc - latestDecision.DecidedAtUtc <= ActiveDischargeTargetMaxAge &&
            latestDecision.Decision.Instruction.DecisionState == BatteryDecisionState.Discharge
            ? latestDecision.Decision.TargetPowerWatts
            : null;
    }

    /// <summary>
    /// Calculates the discharge demand while keeping the grid power deadband as safety distance to battery feed-in.
    /// </summary>
    /// <remarks>
    /// The DC-to-AC conversion only estimates inverter losses. Keeping the deadband as import margin prevents that an overestimated
    /// demand turns into battery feed-in, which is forbidden. Without measured battery power the previous behavior is kept.
    /// </remarks>
    private static int CalculateDischargeDemandWatts(
        int gridPowerWithoutBatteryWatts,
        CurrentSiteTelemetry siteTelemetry,
        int gridPowerDeadbandWatts)
    {
        // Charging of a supplementary storage from the site is never covered; otherwise both batteries pump energy into each other.
        var demandWatts = gridPowerWithoutBatteryWatts - siteTelemetry.SupplementaryBatteryChargeFromSiteWatts;

        return siteTelemetry.CurrentBatteryPowerWatts is null
            ? Math.Max(0, demandWatts)
            : Math.Max(0, demandWatts - gridPowerDeadbandWatts);
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
        int gridPowerWithoutBatteryWatts,
        TibberPriceForecastSlot? currentPriceSlot,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal feedInCompensationPricePerKwh,
        CancellationToken cancellationToken)
    {
        var targetPowerWatts = gridExportAbsorptionPolicy.CalculateTargetPowerWatts(
            gridPowerWithoutBatteryWatts,
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
                    "Die Decision Engine haelt Kapazitaet fuer spaetere negative Strompreise frei und bleibt deshalb trotz aktuellem Netzexport im Idle-Zustand.");

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
                $"Aktueller Überschuss ohne Akku von {Math.Abs(gridPowerWithoutBatteryWatts)} Watt wird in die Batterie geladen, damit keine Einspeisung stehen bleibt.")
        };
        AddChargePowerLimitReason(
            reasons,
            calculatedTargetPowerWatts: Math.Abs(gridPowerWithoutBatteryWatts),
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
        int plannedTargetPowerWatts,
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

        // The plan only charges the energy that is actually needed; the capacity limit still applies to the live state of charge.
        var calculatedTargetPowerWatts = Math.Min(
            plannedTargetPowerWatts,
            CalculatePowerWatts(calculatedChargeInputEnergyKwh, currentPriceSlot.TimeSlot.Duration));
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

        var ruleId = targetStateOfChargePercent < 100m &&
            targetPowerWatts < batteryConfiguration.MaximumChargePowerWatts &&
            targetPowerWatts < plannedTargetPowerWatts
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
        int dischargeDemandWatts,
        TibberPriceForecastSlot? currentPriceSlot,
        BatteryDecisionReason baseReason,
        decimal reserveStateOfChargePercent,
        CancellationToken cancellationToken)
    {
        var validToUtc = currentPriceSlot?.TimeSlot.EndsAtUtc ?? decidedAtUtc.AddMinutes(15);
        var slotDuration = currentPriceSlot?.TimeSlot.Duration ?? TimeSpan.FromMinutes(15);
        var tibberPricePerKwh = currentPriceSlot?.TotalPricePerKwh;
        var tibberPriceCurrency = currentPriceSlot?.Currency;
        var powerLimitedTargetWatts = dischargePowerLimiter.CalculateTargetPowerWatts(
            dischargeDemandWatts,
            batteryConfiguration);

        if (powerLimitedTargetWatts <= 0)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                validToUtc,
                batteryState,
                siteTelemetry,
                tibberPricePerKwh,
                tibberPriceCurrency,
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.NoGridImportForDischarge,
                    "Ohne aktuellen Netzbezug wird nicht entladen, damit keine Einspeisung ins Netz entsteht."),
                cancellationToken);
        }

        var currentBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * batteryState.StateOfChargePercent / 100m;
        var minimumBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * reserveStateOfChargePercent / 100m;
        var singleDirectionEfficiency = CalculateSingleDirectionEfficiency(batteryConfiguration);
        var availableLoadCoverageKwh = Math.Max(0m, (currentBatteryEnergyKwh - minimumBatteryEnergyKwh) * singleDirectionEfficiency);
        var maximumEnergyLimitedWatts = CalculatePowerWatts(availableLoadCoverageKwh, slotDuration);
        var targetPowerWatts = Math.Min(powerLimitedTargetWatts, maximumEnergyLimitedWatts);

        if (targetPowerWatts <= 0)
        {
            return await SaveIdleDecisionAsync(
                decidedAtUtc,
                validToUtc,
                batteryState,
                siteTelemetry,
                tibberPricePerKwh,
                tibberPriceCurrency,
                new BatteryDecisionReason(
                    BatteryForecastRuleIds.MinimumSocReserve,
                    $"Die Entlade-Reserve von {reserveStateOfChargePercent:0.#} Prozent ist erreicht. Die Decision Engine entlädt deshalb nicht weiter."),
                cancellationToken);
        }

        return await SaveDecisionAsync(
            decidedAtUtc,
            validToUtc,
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
                targetPowerWatts),
            batteryState,
            siteTelemetry,
            tibberPricePerKwh,
            tibberPriceCurrency,
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
        var stabilizedDecision = await TryCreateDirectionChangeHoldDecisionAsync(
            decidedAtUtc,
            decision,
            cancellationToken);
        if (stabilizedDecision is not null)
        {
            decision = stabilizedDecision.Decision;
            reasons = stabilizedDecision.Reasons;
        }

        var inputSummaryJson = JsonSerializer.Serialize(new
        {
            ElectricityPriceProvider = dependencies.TibberPriceForecastProvider.Source,
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

    private async Task<StabilizedDecision?> TryCreateDirectionChangeHoldDecisionAsync(
        DateTimeOffset decidedAtUtc,
        CurrentBatteryDecision proposedDecision,
        CancellationToken cancellationToken)
    {
        if (!CanHoldDirectionChange(proposedDecision))
        {
            return null;
        }

        var settings = await GetDirectionChangeHoldSettingsAsync(cancellationToken);
        if (settings.HoldCycles <= 0 ||
            proposedDecision.TargetPowerWatts > settings.MaximumNewPowerWatts)
        {
            return null;
        }

        var recentDecisions = await dependencies.DecisionLogRepository.GetRecentDecisionsAsync(
            settings.HoldCycles + 1,
            cancellationToken);
        var lastDirectionalDecision = recentDecisions
            .Select((entry, index) => new { Entry = entry, CyclesSince = index })
            .FirstOrDefault(item => item.Entry.Decision.Instruction.DecisionState != BatteryDecisionState.Idle);

        if (lastDirectionalDecision is null ||
            lastDirectionalDecision.CyclesSince >= settings.HoldCycles ||
            lastDirectionalDecision.Entry.Decision.TargetPowerWatts < settings.MinimumPreviousPowerWatts ||
            !IsOppositeDirection(lastDirectionalDecision.Entry.Decision, proposedDecision))
        {
            return null;
        }

        var previousState = lastDirectionalDecision.Entry.Decision.Instruction.DecisionState == BatteryDecisionState.Charge
            ? "Laden"
            : "Entladen";
        var proposedState = proposedDecision.Instruction.DecisionState == BatteryDecisionState.Charge
            ? "Laden"
            : "Entladen";

        return new StabilizedDecision(
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                targetPowerWatts: 0),
            new[]
            {
                new BatteryDecisionReason(
                    CurrentBatteryDecisionRuleIds.DirectionChangeHold,
                    $"Kleine Gegenentscheidung wird geglaettet: vorher {previousState} mit {lastDirectionalDecision.Entry.Decision.TargetPowerWatts} Watt, jetzt {proposedState} mit {proposedDecision.TargetPowerWatts} Watt. Die Decision Engine wartet Zyklus {lastDirectionalDecision.CyclesSince + 1} von {settings.HoldCycles}, bevor sie die Richtung wechselt.")
            });
    }

    private static bool CanHoldDirectionChange(CurrentBatteryDecision proposedDecision)
    {
        return proposedDecision.Instruction.DecisionState == BatteryDecisionState.Discharge ||
            proposedDecision.Instruction is { DecisionState: BatteryDecisionState.Charge, ChargeSource: BatteryChargeSource.PV };
    }

    private static bool IsOppositeDirection(
        CurrentBatteryDecision previousDecision,
        CurrentBatteryDecision proposedDecision)
    {
        return previousDecision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Discharge => proposedDecision.Instruction is
                { DecisionState: BatteryDecisionState.Charge, ChargeSource: BatteryChargeSource.PV },
            BatteryDecisionState.Charge when previousDecision.Instruction.ChargeSource == BatteryChargeSource.PV =>
                proposedDecision.Instruction.DecisionState == BatteryDecisionState.Discharge,
            _ => false
        };
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

    private async Task<DirectionChangeHoldSettings> GetDirectionChangeHoldSettingsAsync(CancellationToken cancellationToken)
    {
        return new DirectionChangeHoldSettings(
            await GetRequiredNonNegativeIntegerSettingAsync(
                ControllerSettingDefaults.DecisionDirectionChangeHoldCyclesKey,
                "Die Richtungswechsel-Wartezyklen",
                cancellationToken),
            await GetRequiredNonNegativeIntegerSettingAsync(
                ControllerSettingDefaults.DecisionDirectionChangeMinimumPreviousPowerWattsKey,
                "Die Richtungswechsel-Altleistung",
                cancellationToken),
            await GetRequiredNonNegativeIntegerSettingAsync(
                ControllerSettingDefaults.DecisionDirectionChangeMaximumNewPowerWattsKey,
                "Die Richtungswechsel-Neuleistung",
                cancellationToken));
    }

    private async Task<int> GetRequiredNonNegativeIntegerSettingAsync(
        string key,
        string displayName,
        CancellationToken cancellationToken)
    {
        var setting = await dependencies.ControllerSettingStore.GetSettingAsync(key, cancellationToken);
        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException($"{displayName} sind nicht konfiguriert.");
        }

        if (!int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"{displayName} muessen als ganze Zahl konfiguriert sein.");
        }

        if (value < 0)
        {
            throw new InvalidOperationException($"{displayName} duerfen nicht negativ sein.");
        }

        return value;
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

    private sealed record DirectionChangeHoldSettings(
        int HoldCycles,
        int MinimumPreviousPowerWatts,
        int MaximumNewPowerWatts);

    private sealed record PlannedForecastSlot(
        BatteryForecastEntry? Entry,
        string? FailureMessage);

    private sealed record StabilizedDecision(
        CurrentBatteryDecision Decision,
        IReadOnlyList<BatteryDecisionReason> Reasons);
}
