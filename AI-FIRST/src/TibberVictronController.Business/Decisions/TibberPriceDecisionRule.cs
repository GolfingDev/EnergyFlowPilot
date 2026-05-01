using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Classifies Tibber price slots into cheap, neutral and expensive phases for the Decision Engine.
/// </summary>
public sealed class TibberPriceDecisionRule
{
    private const string RuleName = "TibberPriceDistribution";
    private const decimal EmptyBatterySocPercent = 0m;
    private const decimal LowBatterySocPercent = 25m;
    private const decimal HighBatterySocPercent = 80m;
    private const decimal FullBatterySocPercent = 100m;

    /// <summary>
    /// Evaluates the current Tibber price and battery state against the forecast distribution and returns a justified instruction.
    /// </summary>
    public BatteryDecisionRuleResult Evaluate(
        DateTimeOffset decisionTimeUtc,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        BatteryState batteryState)
    {
        if (decisionTimeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Entscheidungszeitpunkt muss in UTC angegeben sein.", nameof(decisionTimeUtc));
        }

        if (priceForecast is null)
        {
            throw new ArgumentNullException(nameof(priceForecast), "Die Tibber-Preisprognose darf nicht null sein.");
        }

        if (batteryState is null)
        {
            throw new ArgumentNullException(nameof(batteryState), "Der Akkuladestand darf nicht null sein.");
        }

        if (priceForecast.Count == 0)
        {
            return CreateIdleResult("Es liegen keine Tibber-Preise vor. Die Decision Engine bleibt deshalb im Idle-Zustand.");
        }

        var currentPriceSlot = priceForecast.SingleOrDefault(priceSlot =>
            priceSlot.TimeSlot.StartsAtUtc <= decisionTimeUtc &&
            decisionTimeUtc < priceSlot.TimeSlot.EndsAtUtc);

        if (currentPriceSlot is null)
        {
            return CreateIdleResult("Fuer den aktuellen Entscheidungszeitpunkt liegt kein Tibber-Preis vor. Die Decision Engine bleibt deshalb im Idle-Zustand.");
        }

        var priceBoundaries = CalculatePriceBoundaries(priceForecast);
        var chargePriceLimit = CalculateSocAwareChargePriceLimit(priceBoundaries, batteryState);

        if (currentPriceSlot.TotalPricePerKwh < 0m)
        {
            if (batteryState.StateOfChargePercent >= FullBatterySocPercent)
            {
                return CreateIdleResult("Der Tibber-Preis ist negativ, aber der Akku ist voll und kann nicht weiter geladen werden.");
            }

            return CreateResult(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                $"Der aktuelle Tibber-Preis {currentPriceSlot.TotalPricePerKwh:0.0000} ist negativ. Die Decision Engine nutzt diesen Slot deshalb zum Laden aus dem Netz.");
        }

        if (currentPriceSlot.TotalPricePerKwh <= chargePriceLimit)
        {
            if (batteryState.StateOfChargePercent >= FullBatterySocPercent)
            {
                return CreateIdleResult("Der Akku ist voll und kann nicht weiter geladen werden. Die Decision Engine bleibt deshalb im Idle-Zustand.");
            }

            return CreateResult(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                CreateChargeReason(currentPriceSlot, batteryState, chargePriceLimit));
        }

        if (currentPriceSlot.TotalPricePerKwh >= priceBoundaries.ExpensivePriceLimit)
        {
            if (batteryState.StateOfChargePercent <= EmptyBatterySocPercent)
            {
                return CreateIdleResult("Der Akku ist leer und kann nicht entladen werden. Die Decision Engine bleibt deshalb im Idle-Zustand.");
            }

            return CreateResult(
                new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
                $"Der aktuelle Tibber-Preis {currentPriceSlot.TotalPricePerKwh:0.0000} liegt in der teuren Tibber-Preisphase. Entladen vermeidet teuren Netzbezug.");
        }

        if (batteryState.StateOfChargePercent >= HighBatterySocPercent &&
            currentPriceSlot.TotalPricePerKwh <= priceBoundaries.CheapPriceLimit)
        {
            return CreateIdleResult(
                $"Der aktuelle Tibber-Preis {currentPriceSlot.TotalPricePerKwh:0.0000} ist nur normal guenstig. Wegen hohem Akkuladestand von {batteryState.StateOfChargePercent:0.#} Prozent wird nicht aus dem Netz geladen.");
        }

        return CreateIdleResult(
            $"Der aktuelle Tibber-Preis {currentPriceSlot.TotalPricePerKwh:0.0000} liegt in der neutralen Tibber-Preisphase. Die Decision Engine bleibt idle.");
    }

    private static PriceBoundaries CalculatePriceBoundaries(IReadOnlyList<TibberPriceForecastSlot> priceForecast)
    {
        var orderedPrices = priceForecast
            .Select(priceSlot => priceSlot.TotalPricePerKwh)
            .Order()
            .ToArray();

        // The first rule uses thirds of the available forecast window as a transparent starting point.
        var thirdOfForecastWindow = Math.Max(1, orderedPrices.Length / 3);
        var cheapBoundaryIndex = Math.Min(orderedPrices.Length - 1, thirdOfForecastWindow - 1);
        var expensiveBoundaryIndex = Math.Max(0, orderedPrices.Length - thirdOfForecastWindow);
        var neutralBoundaryIndex = Math.Max(0, expensiveBoundaryIndex - 1);

        return new PriceBoundaries(
            orderedPrices[0],
            orderedPrices[cheapBoundaryIndex],
            orderedPrices[neutralBoundaryIndex],
            orderedPrices[expensiveBoundaryIndex]);
    }

    private static decimal CalculateSocAwareChargePriceLimit(
        PriceBoundaries priceBoundaries,
        BatteryState batteryState)
    {
        if (batteryState.StateOfChargePercent <= LowBatterySocPercent)
        {
            return priceBoundaries.NeutralPriceLimit;
        }

        if (batteryState.StateOfChargePercent >= HighBatterySocPercent)
        {
            return priceBoundaries.LowestPrice;
        }

        return priceBoundaries.CheapPriceLimit;
    }

    private static string CreateChargeReason(
        TibberPriceForecastSlot currentPriceSlot,
        BatteryState batteryState,
        decimal chargePriceLimit)
    {
        var chargeReasonPrefix = batteryState.StateOfChargePercent <= LowBatterySocPercent
            ? $"Der niedrige Akkuladestand von {batteryState.StateOfChargePercent:0.#} Prozent macht Laden bereits bis {chargePriceLimit:0.0000} EUR/kWh sinnvoll."
            : $"Der aktuelle Tibber-Preis {currentPriceSlot.TotalPricePerKwh:0.0000} liegt in der guenstigen Tibber-Preisphase.";

        return $"{chargeReasonPrefix} Laden aus dem Netz senkt den erwarteten Durchschnittspreis.";
    }

    private static BatteryDecisionRuleResult CreateIdleResult(string reasonMessage)
    {
        return CreateResult(
            new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
            reasonMessage);
    }

    private static BatteryDecisionRuleResult CreateResult(
        BatteryDecisionInstruction instruction,
        string reasonMessage)
    {
        return new BatteryDecisionRuleResult(
            instruction,
            new[] { new BatteryDecisionReason(RuleName, reasonMessage) });
    }

    private sealed record PriceBoundaries(
        decimal LowestPrice,
        decimal CheapPriceLimit,
        decimal NeutralPriceLimit,
        decimal ExpensivePriceLimit);
}
