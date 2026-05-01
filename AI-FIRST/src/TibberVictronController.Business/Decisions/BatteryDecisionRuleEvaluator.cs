using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Applies high-priority Decision Engine rules before falling back to Tibber price evaluation.
/// </summary>
public sealed class BatteryDecisionRuleEvaluator
{
    private const string RuleName = "PvSurplusPriority";
    private const decimal FullBatterySocPercent = 100m;

    private readonly TibberPriceDecisionRule tibberPriceDecisionRule;

    public BatteryDecisionRuleEvaluator()
        : this(new TibberPriceDecisionRule())
    {
    }

    internal BatteryDecisionRuleEvaluator(TibberPriceDecisionRule tibberPriceDecisionRule)
    {
        this.tibberPriceDecisionRule = tibberPriceDecisionRule;
    }

    /// <summary>
    /// Evaluates PV surplus first because self-generated energy has priority over the Tibber price strategy.
    /// </summary>
    public BatteryDecisionRuleResult Evaluate(
        DateTimeOffset decisionTimeUtc,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        PvYieldForecastSlot pvYieldSlot,
        ConsumptionForecastSlot consumptionSlot,
        BatteryState batteryState)
    {
        if (pvYieldSlot is null)
        {
            throw new ArgumentNullException(nameof(pvYieldSlot), "Der PV-Ertrag darf nicht null sein.");
        }

        if (consumptionSlot is null)
        {
            throw new ArgumentNullException(nameof(consumptionSlot), "Der erwartete Verbrauch darf nicht null sein.");
        }

        if (batteryState is null)
        {
            throw new ArgumentNullException(nameof(batteryState), "Der Akkuladestand darf nicht null sein.");
        }

        if (pvYieldSlot.TimeSlot != consumptionSlot.TimeSlot)
        {
            throw new ArgumentException("PV-Ertrag und Verbrauch muessen denselben Forecast-Zeitabschnitt verwenden.", nameof(consumptionSlot));
        }

        if (!SlotContainsDecisionTime(pvYieldSlot.TimeSlot, decisionTimeUtc))
        {
            throw new ArgumentException("Der Entscheidungszeitpunkt muss im Forecast-Zeitabschnitt fuer PV-Ertrag und Verbrauch liegen.", nameof(decisionTimeUtc));
        }

        // PV surplus is evaluated before price rules so cheap or expensive Tibber phases cannot hide self-generated surplus.
        if (pvYieldSlot.ExpectedPvYieldKwh > consumptionSlot.ExpectedConsumptionKwh)
        {
            if (batteryState.StateOfChargePercent >= FullBatterySocPercent)
            {
                return CreateResult(
                    new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                    "Der PV-Ertrag uebersteigt den erwarteten Verbrauch, aber der Akku ist voll und kann nicht weiter geladen werden.");
            }

            return CreateResult(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
                $"Der PV-Ertrag {pvYieldSlot.ExpectedPvYieldKwh:0.0000} kWh uebersteigt den erwarteten Verbrauch {consumptionSlot.ExpectedConsumptionKwh:0.0000} kWh. Die Decision Engine laedt deshalb aus PV.");
        }

        return tibberPriceDecisionRule.Evaluate(decisionTimeUtc, priceForecast, batteryState);
    }

    private static bool SlotContainsDecisionTime(ForecastTimeSlot timeSlot, DateTimeOffset decisionTimeUtc)
    {
        return timeSlot.StartsAtUtc <= decisionTimeUtc && decisionTimeUtc < timeSlot.EndsAtUtc;
    }

    private static BatteryDecisionRuleResult CreateResult(
        BatteryDecisionInstruction instruction,
        string reasonMessage)
    {
        return new BatteryDecisionRuleResult(
            instruction,
            new[] { new BatteryDecisionReason(RuleName, reasonMessage) });
    }
}
