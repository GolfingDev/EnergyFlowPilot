using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Calculates daily monetary savings summaries from battery charge and discharge slot movements.
/// </summary>
public sealed class BatterySavingsCalculator
{
    /// <summary>
    /// Groups slot movements by reporting day and calculates cost, avoided cost and net savings.
    /// </summary>
    public IReadOnlyList<BatterySavingsDailySummary> CalculateDailySummaries(
        IReadOnlyList<BatterySavingsSlotMovement> movements,
        BatterySavingsCalculationOptions options)
    {
        ValidateInputs(movements, options);

        return movements
            .Where(movement => movement.Instruction.DecisionState != BatteryDecisionState.Idle)
            .GroupBy(movement => new
            {
                AccountingDate = ResolveAccountingDate(movement, options.ReportingTimeZone!),
                movement.Currency
            })
            .OrderBy(group => group.Key.AccountingDate)
            .Select(group => CreateSummary(new DailySummaryCalculationInput
            {
                AccountingDate = group.Key.AccountingDate,
                Currency = group.Key.Currency,
                Movements = group.ToArray(),
                UpdatedAtUtc = options.UpdatedAtUtc
            }))
            .ToArray();
    }

    private static BatterySavingsDailySummary CreateSummary(DailySummaryCalculationInput input)
    {
        var values = new BatterySavingsDailySummaryValues
        {
            AccountingDate = input.AccountingDate,
            Currency = input.Currency,
            GridChargedEnergyKwh = SumEnergy(input.Movements, IsGridCharge),
            GridChargeCost = SumMoney(input.Movements, IsGridCharge, movement => movement.TibberPricePerKwh),
            PvChargedEnergyKwh = SumEnergy(input.Movements, IsPvCharge),
            PvOpportunityCost = SumMoney(input.Movements, IsPvCharge, movement => movement.PvSalePricePerKwh),
            DischargedEnergyKwh = SumEnergy(input.Movements, IsDischarge),
            DischargeAvoidedCost = SumMoney(input.Movements, IsDischarge, movement => movement.TibberPricePerKwh),
            UpdatedAtUtc = input.UpdatedAtUtc
        };
        var completeValues = new BatterySavingsDailySummaryValues
        {
            AccountingDate = values.AccountingDate,
            Currency = values.Currency,
            GridChargedEnergyKwh = values.GridChargedEnergyKwh,
            GridChargeCost = values.GridChargeCost,
            PvChargedEnergyKwh = values.PvChargedEnergyKwh,
            PvOpportunityCost = values.PvOpportunityCost,
            DischargedEnergyKwh = values.DischargedEnergyKwh,
            DischargeAvoidedCost = values.DischargeAvoidedCost,
            NetSavings = values.DischargeAvoidedCost - values.GridChargeCost - values.PvOpportunityCost,
            UpdatedAtUtc = values.UpdatedAtUtc
        };

        return new BatterySavingsDailySummary(completeValues);
    }

    private static void ValidateInputs(
        IReadOnlyList<BatterySavingsSlotMovement> movements,
        BatterySavingsCalculationOptions options)
    {
        if (movements is null)
        {
            throw new ArgumentNullException(nameof(movements), "Die Batterie-Ersparnis-Bewegungen duerfen nicht null sein.");
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options), "Die Batterie-Ersparnis-Optionen duerfen nicht null sein.");
        }

        if (options.ReportingTimeZone is null)
        {
            throw new ArgumentException("Die Reporting-Zeitzone fuer Batterie-Ersparnis muss angegeben werden.", nameof(options));
        }

        if (options.UpdatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Aktualisierungszeitpunkt fuer Batterie-Ersparnis muss in UTC angegeben sein.", nameof(options));
        }

        foreach (var movement in movements)
        {
            ValidateMovement(movement);
        }
    }

    private static void ValidateMovement(BatterySavingsSlotMovement movement)
    {
        if (movement.TimeSlot is null)
        {
            throw new ArgumentException("Eine Batterie-Ersparnis-Bewegung braucht einen Zeitabschnitt.");
        }

        if (movement.Instruction is null)
        {
            throw new ArgumentException("Eine Batterie-Ersparnis-Bewegung braucht eine Entscheidung.");
        }

        if (movement.TargetPowerWatts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(movement), "Die Ziel-Leistung fuer Batterie-Ersparnis darf nicht negativ sein.");
        }

        if (string.IsNullOrWhiteSpace(movement.Currency))
        {
            throw new ArgumentException("Eine Batterie-Ersparnis-Bewegung braucht eine Waehrung.");
        }
    }

    private static DateOnly ResolveAccountingDate(
        BatterySavingsSlotMovement movement,
        TimeZoneInfo reportingTimeZone)
    {
        var localStart = TimeZoneInfo.ConvertTime(movement.TimeSlot.StartsAtUtc, reportingTimeZone);

        return DateOnly.FromDateTime(localStart.DateTime);
    }

    private static decimal SumEnergy(
        IEnumerable<BatterySavingsSlotMovement> movements,
        Func<BatterySavingsSlotMovement, bool> predicate)
    {
        return movements
            .Where(predicate)
            .Sum(CalculateEnergyKwh);
    }

    private static decimal SumMoney(
        IEnumerable<BatterySavingsSlotMovement> movements,
        Func<BatterySavingsSlotMovement, bool> predicate,
        Func<BatterySavingsSlotMovement, decimal> priceSelector)
    {
        return movements
            .Where(predicate)
            .Sum(movement => CalculateEnergyKwh(movement) * priceSelector(movement));
    }

    private static decimal CalculateEnergyKwh(BatterySavingsSlotMovement movement)
    {
        return movement.TargetPowerWatts / 1000m * (decimal)movement.TimeSlot.Duration.TotalHours;
    }

    private static bool IsGridCharge(BatterySavingsSlotMovement movement)
    {
        return movement.Instruction.DecisionState == BatteryDecisionState.Charge &&
            movement.Instruction.ChargeSource == BatteryChargeSource.Grid;
    }

    private static bool IsPvCharge(BatterySavingsSlotMovement movement)
    {
        return movement.Instruction.DecisionState == BatteryDecisionState.Charge &&
            movement.Instruction.ChargeSource == BatteryChargeSource.PV;
    }

    private static bool IsDischarge(BatterySavingsSlotMovement movement)
    {
        return movement.Instruction.DecisionState == BatteryDecisionState.Discharge;
    }

    private sealed class DailySummaryCalculationInput
    {
        public DateOnly AccountingDate { get; init; }

        public string Currency { get; init; } = string.Empty;

        public IReadOnlyList<BatterySavingsSlotMovement> Movements { get; init; } = Array.Empty<BatterySavingsSlotMovement>();

        public DateTimeOffset UpdatedAtUtc { get; init; }
    }
}
