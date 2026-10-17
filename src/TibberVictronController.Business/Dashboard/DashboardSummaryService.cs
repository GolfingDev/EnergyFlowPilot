using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Dashboard;

/// <summary>
/// Builds the control-room dashboard: what happens now, whether it is healthy, what is planned and how the last hours went.
/// </summary>
/// <remarks>
/// All values that describe real power flows come from measured samples, never from requested decision targets. The checks
/// encode the failure modes seen on this site: battery feed-in and the Victron battery charging the E3/DC battery.
/// </remarks>
public sealed class DashboardSummaryService : IDashboardSummaryService
{
    private static readonly TimeSpan LiveSampleMaxAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PlanHorizon = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMinutes(1);
    private const decimal BatteryActivityThresholdWatts = 100m;
    private const decimal FeedInThresholdWatts = 50m;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DateTimeOffset, decimal?> LoggedPriceCache = new();

    private readonly DashboardSummaryServiceDependencies dependencies;

    public DashboardSummaryService(DashboardSummaryServiceDependencies dependencies)
    {
        this.dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies), "Die Abhaengigkeiten fuer die Dashboard-Zusammenfassung duerfen nicht null sein.");
    }

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = dependencies.UtcClock.UtcNow;
        var latestDecision = (await dependencies.DecisionLogRepository.GetRecentDecisionsAsync(1, cancellationToken)).FirstOrDefault();
        var recentSamples = await dependencies.LiveConsumptionRepository.GetSamplesAsync(
            nowUtc.Subtract(LiveSampleMaxAge),
            nowUtc.AddSeconds(1),
            cancellationToken);
        var latestSample = recentSamples.Count == 0 ? null : recentSamples[^1];
        var live = latestSample is null ? null : CreateLiveState(latestSample);
        var current = CreateCurrentState(latestDecision);

        IReadOnlyList<BatteryForecastEntry> planEntries = Array.Empty<BatteryForecastEntry>();
        string? planError = null;
        try
        {
            var slotStartsAtUtc = new DateTimeOffset(
                nowUtc.UtcTicks - nowUtc.UtcTicks % TimeSpan.FromMinutes(15).Ticks,
                TimeSpan.Zero);
            var forecast = await dependencies.BatteryForecastService.CalculateForecastAsync(
                slotStartsAtUtc,
                nowUtc.Add(PlanHorizon),
                cancellationToken);
            planEntries = forecast.Entries;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            planError = exception.Message;
        }

        var planSlots = planEntries
            .Select(entry => new DashboardPlanSlot(
                entry.TimeSlot,
                entry.TibberPricePerKwh,
                entry.StateOfChargeAfterPercent,
                entry.ExpectedPvYieldKwh,
                entry.ExpectedConsumptionKwh,
                Categorize(entry.Decision.Instruction, entry.Reasons.FirstOrDefault()?.RuleName),
                entry.IsPriceEstimated))
            .ToArray();
        var segments = CreateSegments(planEntries);
        var checks = CreateChecks(new CheckInput(nowUtc, latestSample, current, segments, planError));

        return new DashboardSummary(nowUtc, current, live, checks, planSlots, segments);
    }

    public async Task<DashboardReview> GetReviewAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        var configuration = await dependencies.BatteryConfigurationProvider.GetBatteryConfigurationAsync(cancellationToken);
        var samples = await dependencies.LiveConsumptionRepository.GetSamplesAsync(startsAtUtc, endsAtUtc, cancellationToken);
        var review = CalculateReview(startsAtUtc, endsAtUtc, samples, configuration.PlanningMinimumStateOfChargePercent);

        var prices = await LoadSlotPricesAsync(startsAtUtc, endsAtUtc, cancellationToken);
        var comparison = StrategyComparisonCalculator.Calculate(new StrategyComparisonInput
        {
            Samples = samples,
            GetSlotPricePerKwh = slotStartsAtUtc => prices.TryGetValue(slotStartsAtUtc, out var price) ? price : null,
            BatteryConfiguration = configuration,
            FeedInCompensationPricePerKwh = await GetDecimalSettingAsync(ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey, cancellationToken),
            GridPowerDeadbandWatts = (int)await GetDecimalSettingAsync(ControllerSettingDefaults.TelemetryGridPowerDeadbandWattsKey, cancellationToken)
        });

        return review with
        {
            Costs = comparison.Total,
            Hours = review.Hours
                .Select(hour => comparison.Hours.TryGetValue(hour.StartsAtUtc, out var cost)
                    ? hour with { ActualCost = cost.ActualCost, BaselineCost = cost.BaselineCost }
                    : hour)
                .ToArray()
        };
    }

    /// <summary>
    /// Collects the slot prices of the review period: Tibber still delivers today, older slots come from the decision log.
    /// </summary>
    private async Task<IReadOnlyDictionary<DateTimeOffset, decimal>> LoadSlotPricesAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken)
    {
        var slotDuration = TimeSpan.FromMinutes(15);
        var firstSlotUtc = new DateTimeOffset(startsAtUtc.UtcTicks - startsAtUtc.UtcTicks % slotDuration.Ticks, TimeSpan.Zero);
        var prices = new Dictionary<DateTimeOffset, decimal>();

        try
        {
            var tibberSlots = await dependencies.TibberPriceForecastProvider.GetPriceForecastAsync(firstSlotUtc, endsAtUtc, cancellationToken);
            foreach (var slot in tibberSlots)
            {
                prices[slot.TimeSlot.StartsAtUtc] = slot.TotalPricePerKwh;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Without Tibber the decision log still knows the prices of all slots the controller has seen.
        }

        var nowUtc = dependencies.UtcClock.UtcNow;
        for (var slotStartsAtUtc = firstSlotUtc; slotStartsAtUtc < endsAtUtc; slotStartsAtUtc = slotStartsAtUtc.Add(slotDuration))
        {
            if (prices.ContainsKey(slotStartsAtUtc))
            {
                continue;
            }

            if (LoggedPriceCache.TryGetValue(slotStartsAtUtc, out var cachedPrice))
            {
                if (cachedPrice is not null)
                {
                    prices[slotStartsAtUtc] = cachedPrice.Value;
                }

                continue;
            }

            var decisions = await dependencies.DecisionLogRepository.GetDecisionsAsync(slotStartsAtUtc, slotStartsAtUtc.Add(slotDuration).AddTicks(-1), 1, cancellationToken);
            var loggedPrice = decisions.FirstOrDefault(decision => decision.TibberPricePerKwh is not null)?.TibberPricePerKwh;

            // Finished slots never change, so the five-minute review refresh does not repeat these queries.
            if (slotStartsAtUtc.Add(slotDuration) <= nowUtc)
            {
                LoggedPriceCache[slotStartsAtUtc] = loggedPrice;
            }

            if (loggedPrice is not null)
            {
                prices[slotStartsAtUtc] = loggedPrice.Value;
            }
        }

        return prices;
    }

    private async Task<decimal> GetDecimalSettingAsync(string settingKey, CancellationToken cancellationToken)
    {
        var setting = await dependencies.ControllerSettingStore.GetSettingAsync(settingKey, cancellationToken);

        return setting is { IsConfigured: true } &&
               decimal.TryParse(setting.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    /// <summary>
    /// Integrates measured samples into energies; gaps longer than one minute are capped so outages do not inflate the totals.
    /// </summary>
    public static DashboardReview CalculateReview(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        IReadOnlyList<LiveConsumptionSample> samples,
        decimal planningMinimumStateOfChargePercent)
    {
        var total = new EnergyAccumulator();
        var hours = new SortedDictionary<DateTimeOffset, EnergyAccumulator>();

        for (var index = 0; index < samples.Count - 1; index++)
        {
            var sample = samples[index];
            var gap = samples[index + 1].MeasuredAtUtc - sample.MeasuredAtUtc;
            var durationHours = (decimal)(gap < MaximumSampleGap ? gap : MaximumSampleGap).TotalHours;
            var hourStartsAtUtc = new DateTimeOffset(
                sample.MeasuredAtUtc.UtcTicks - sample.MeasuredAtUtc.UtcTicks % TimeSpan.TicksPerHour,
                TimeSpan.Zero);

            if (!hours.TryGetValue(hourStartsAtUtc, out var hour))
            {
                hour = new EnergyAccumulator();
                hours[hourStartsAtUtc] = hour;
            }

            total.Add(sample, durationHours, planningMinimumStateOfChargePercent);
            hour.Add(sample, durationHours, planningMinimumStateOfChargePercent);
        }

        var supplementarySoc = samples
            .Where(sample => sample.SupplementaryBatterySocPercent is not null)
            .Select(sample => sample.SupplementaryBatterySocPercent!.Value)
            .ToArray();

        return new DashboardReview(
            startsAtUtc,
            endsAtUtc,
            Round(total.BatteryDischargeKwh),
            Round(total.GridImportKwh),
            Round(total.GridImportWhileBatteryAvailableKwh),
            Round(total.GridChargeKwh),
            Round(total.BatteryFeedInKwh),
            Round(total.SupplementaryChargedFromBatteryKwh),
            supplementarySoc.Length == 0 ? null : supplementarySoc[0],
            supplementarySoc.Length == 0 ? null : supplementarySoc[^1],
            hours
                .Select(hour => new DashboardReviewHour(
                    hour.Key,
                    hour.Value.LastBatterySocPercent,
                    Round(hour.Value.GridImportKwh),
                    Round(hour.Value.GridExportKwh),
                    Round(hour.Value.BatteryChargeKwh),
                    Round(hour.Value.BatteryDischargeKwh),
                    Round(hour.Value.BatteryFeedInKwh),
                    Round(hour.Value.SupplementaryChargedFromBatteryKwh)))
                .ToArray());
    }

    /// <summary>
    /// Maps a decision instruction and its rule to the one category the dashboard colours consistently everywhere.
    /// </summary>
    public static DashboardActionCategory Categorize(BatteryDecisionInstruction instruction, string? ruleId)
    {
        return instruction.DecisionState switch
        {
            BatteryDecisionState.Charge when instruction.ChargeSource == BatteryChargeSource.Grid => DashboardActionCategory.GridCharge,
            BatteryDecisionState.Charge => DashboardActionCategory.PvCharge,
            BatteryDecisionState.Discharge => DashboardActionCategory.Discharge,
            _ => ruleId switch
            {
                BatteryForecastRuleIds.HoldForExpensiveSlot or
                BatteryForecastRuleIds.EndSocReserve or
                BatteryForecastRuleIds.WaitForNegativePriceWindow => DashboardActionCategory.Hold,
                BatteryForecastRuleIds.PlanningMaximumSocHeadroom or
                BatteryForecastRuleIds.BatteryFullIdle or
                BatteryForecastRuleIds.BatteryFullPvSurplus or
                CurrentBatteryDecisionRuleIds.BatteryFull => DashboardActionCategory.Full,
                _ => DashboardActionCategory.Idle
            }
        };
    }

    private static DashboardCurrentState CreateCurrentState(DecisionLogEntry? latestDecision)
    {
        if (latestDecision is null)
        {
            return new DashboardCurrentState(DashboardActionCategory.Idle, 0, null, "Es liegt noch keine Entscheidung vor.", null, null, null);
        }

        var reason = latestDecision.Reasons.FirstOrDefault();

        return new DashboardCurrentState(
            Categorize(latestDecision.Decision.Instruction, reason?.RuleName),
            latestDecision.Decision.TargetPowerWatts,
            reason?.RuleName,
            reason?.Message,
            latestDecision.TibberPricePerKwh,
            latestDecision.TibberPriceCurrency,
            latestDecision.DecidedAtUtc);
    }

    private static DashboardLiveState CreateLiveState(LiveConsumptionSample sample)
    {
        // The stored value is tailored to the consumption profile and leaves out what the E3/DC battery covers.
        // The live view shows the real house demand instead: grid minus both batteries plus E3/DC PV.
        var houseConsumptionWatts = sample is { GridPowerWatts: { } grid, BatteryPowerWatts: { } battery, SupplementaryBatteryPowerWatts: { } supplementaryBattery }
            ? Math.Max(0m, grid - battery - supplementaryBattery + Math.Max(0m, sample.SupplementaryPvProductionWatts ?? 0m))
            : sample.HouseConsumptionWatts;

        return new DashboardLiveState(
            sample.MeasuredAtUtc,
            sample.GridPowerWatts,
            houseConsumptionWatts,
            sample.BatteryPowerWatts,
            sample.BatterySocPercent,
            sample.SupplementaryPvProductionWatts,
            sample.SupplementaryBatteryPowerWatts,
            sample.SupplementaryBatterySocPercent);
    }

    private static IReadOnlyList<DashboardPlanSegment> CreateSegments(IReadOnlyList<BatteryForecastEntry> entries)
    {
        var segments = new List<DashboardPlanSegment>();
        var index = 0;

        while (index < entries.Count)
        {
            var first = entries[index];
            var category = Categorize(first.Decision.Instruction, first.Reasons.FirstOrDefault()?.RuleName);
            var group = entries
                .Skip(index)
                .TakeWhile(entry => Categorize(entry.Decision.Instruction, entry.Reasons.FirstOrDefault()?.RuleName) == category)
                .ToArray();
            var reason = first.Reasons.FirstOrDefault();

            segments.Add(new DashboardPlanSegment(
                first.TimeSlot.StartsAtUtc,
                group[^1].TimeSlot.EndsAtUtc,
                category,
                first.StateOfChargeBeforePercent,
                group[^1].StateOfChargeAfterPercent,
                group.Min(entry => entry.TibberPricePerKwh),
                group.Max(entry => entry.TibberPricePerKwh),
                Round(group.Sum(entry => entry.ExpectedPvYieldKwh)),
                Round(group
                    .Where(entry => category == DashboardActionCategory.GridCharge)
                    .Sum(entry => entry.Decision.TargetPowerWatts / 1000m * (decimal)entry.TimeSlot.Duration.TotalHours)),
                reason?.RuleName,
                reason?.Message));

            index += group.Length;
        }

        return segments;
    }

    private static IReadOnlyList<DashboardCheck> CreateChecks(CheckInput input)
    {
        var checks = new List<DashboardCheck>();
        var sample = input.LatestSample;

        if (sample is null || input.NowUtc - sample.MeasuredAtUtc > LiveSampleMaxAge)
        {
            checks.Add(new DashboardCheck(
                "live-data",
                DashboardCheckSeverity.Critical,
                "Es liegen keine aktuellen Messwerte vom Victron vor. Pruefungen und Live-Werte sind nicht verfuegbar."));
        }
        else
        {
            var gridPowerWatts = sample.GridPowerWatts ?? 0m;
            var batteryPowerWatts = sample.BatteryPowerWatts ?? 0m;
            var isDischarging = batteryPowerWatts < -BatteryActivityThresholdWatts;

            checks.Add(isDischarging && gridPowerWatts < -FeedInThresholdWatts
                ? new DashboardCheck("battery-feed-in", DashboardCheckSeverity.Critical, $"Der Akku speist gerade {Math.Round(-gridPowerWatts):0} W ins Netz ein.")
                : new DashboardCheck("battery-feed-in", DashboardCheckSeverity.Ok, "Keine Einspeisung aus dem Akku."));

            if (sample.SupplementaryBatteryPowerWatts is not { } supplementaryBatteryPowerWatts)
            {
                checks.Add(new DashboardCheck(
                    "supplementary-from-battery",
                    DashboardCheckSeverity.Info,
                    "Keine aktuellen E3/DC-Werte. Ob das E3/DC aus dem Victron-Akku laedt, kann nicht geprueft werden."));
            }
            else
            {
                var chargeFromSiteWatts = Math.Max(0m, supplementaryBatteryPowerWatts - Math.Max(0m, sample.SupplementaryPvProductionWatts ?? 0m));
                checks.Add(isDischarging && chargeFromSiteWatts > BatteryActivityThresholdWatts
                    ? new DashboardCheck("supplementary-from-battery", DashboardCheckSeverity.Critical, $"Das E3/DC laedt gerade mit {Math.Round(chargeFromSiteWatts):0} W aus dem Victron-Akku.")
                    : new DashboardCheck("supplementary-from-battery", DashboardCheckSeverity.Ok, "Das E3/DC laedt nicht aus dem Victron-Akku."));
            }
        }

        if (input.Current.Category == DashboardActionCategory.GridCharge)
        {
            checks.Add(new DashboardCheck("grid-charge", DashboardCheckSeverity.Info, $"Netzladung aktiv mit {input.Current.TargetPowerWatts} W."));
        }
        else if (input.Segments.Any(segment => segment.Category == DashboardActionCategory.GridCharge))
        {
            checks.Add(new DashboardCheck("grid-charge", DashboardCheckSeverity.Info, "Netzladung ist fuer spaeter geplant, siehe Plan."));
        }
        else
        {
            checks.Add(new DashboardCheck("grid-charge", DashboardCheckSeverity.Ok, "Keine Netzladung aktiv oder geplant."));
        }

        if (input.PlanError is not null)
        {
            checks.Add(new DashboardCheck("plan", DashboardCheckSeverity.Warning, $"Der Plan konnte nicht berechnet werden: {input.PlanError}"));
        }

        return checks;
    }

    private static decimal Round(decimal value)
    {
        return Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }

    private sealed record CheckInput(
        DateTimeOffset NowUtc,
        LiveConsumptionSample? LatestSample,
        DashboardCurrentState Current,
        IReadOnlyList<DashboardPlanSegment> Segments,
        string? PlanError);

    private sealed class EnergyAccumulator
    {
        public decimal GridImportKwh { get; private set; }

        public decimal GridExportKwh { get; private set; }

        public decimal GridImportWhileBatteryAvailableKwh { get; private set; }

        public decimal BatteryChargeKwh { get; private set; }

        public decimal BatteryDischargeKwh { get; private set; }

        public decimal GridChargeKwh { get; private set; }

        public decimal BatteryFeedInKwh { get; private set; }

        public decimal SupplementaryChargedFromBatteryKwh { get; private set; }

        public decimal? LastBatterySocPercent { get; private set; }

        public void Add(LiveConsumptionSample sample, decimal durationHours, decimal planningMinimumStateOfChargePercent)
        {
            var gridPowerWatts = sample.GridPowerWatts ?? 0m;
            var batteryPowerWatts = sample.BatteryPowerWatts ?? 0m;
            var importWatts = Math.Max(0m, gridPowerWatts);
            var exportWatts = Math.Max(0m, -gridPowerWatts);
            var chargeWatts = Math.Max(0m, batteryPowerWatts);
            var dischargeWatts = Math.Max(0m, -batteryPowerWatts);
            var isCharging = batteryPowerWatts > BatteryActivityThresholdWatts;
            var isDischarging = batteryPowerWatts < -BatteryActivityThresholdWatts;
            var supplementaryChargeFromSiteWatts = sample.SupplementaryBatteryPowerWatts is { } supplementaryBatteryPowerWatts
                ? Math.Max(0m, supplementaryBatteryPowerWatts - Math.Max(0m, sample.SupplementaryPvProductionWatts ?? 0m))
                : 0m;

            GridImportKwh += importWatts * durationHours / 1000m;
            GridExportKwh += exportWatts * durationHours / 1000m;
            BatteryChargeKwh += chargeWatts * durationHours / 1000m;
            BatteryDischargeKwh += dischargeWatts * durationHours / 1000m;

            // Import while the battery still had usable energy is load the battery could have covered.
            if (sample.BatterySocPercent is { } socPercent && socPercent > planningMinimumStateOfChargePercent + 1m)
            {
                GridImportWhileBatteryAvailableKwh += importWatts * durationHours / 1000m;
            }

            // Charging while importing can only come from the grid; PV surplus charging happens while the site exports.
            if (isCharging)
            {
                GridChargeKwh += Math.Min(importWatts, chargeWatts) * durationHours / 1000m;
            }

            if (isDischarging)
            {
                BatteryFeedInKwh += Math.Min(exportWatts, dischargeWatts) * durationHours / 1000m;
                SupplementaryChargedFromBatteryKwh += Math.Min(supplementaryChargeFromSiteWatts, dischargeWatts) * durationHours / 1000m;
            }

            LastBatterySocPercent = sample.BatterySocPercent ?? LastBatterySocPercent;
        }
    }
}
