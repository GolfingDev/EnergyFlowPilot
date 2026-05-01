using Microsoft.Extensions.Options;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public interface IPlanningEngine
{
    Task<IReadOnlyList<PlannedAction>> BuildPlanAsync(
        EnergyState currentState,
        IReadOnlyList<PricePoint> prices,
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken = default);
}

public class PlanningEngine : IPlanningEngine
{
    private readonly ControllerOptions _controllerOptions;
    private readonly PlanningOptions _planningOptions;
    private readonly IConsumptionForecastService _consumptionForecastService;
    private readonly IPvForecastService _pvForecastService;

    public PlanningEngine(
        IOptions<ControllerOptions> controllerOptions,
        IOptions<PlanningOptions> planningOptions,
        IConsumptionForecastService consumptionForecastService,
        IPvForecastService pvForecastService)
    {
        _controllerOptions = controllerOptions.Value;
        _planningOptions = planningOptions.Value;
        _consumptionForecastService = consumptionForecastService;
        _pvForecastService = pvForecastService;
    }

    public async Task<IReadOnlyList<PlannedAction>> BuildPlanAsync(
        EnergyState currentState,
        IReadOnlyList<PricePoint> prices,
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken = default)
    {
        var slots = await BuildForecastSlotsAsync(prices, referenceTime, cancellationToken);
        if (slots.Count == 0)
        {
            return Array.Empty<PlannedAction>();
        }

        var cheapThreshold = GetQuantile(slots.Select(x => x.Price), _planningOptions.CheapQuantile) + _controllerOptions.BaseCheapPriceTolerance;
        var expensiveThreshold = GetQuantile(slots.Select(x => x.Price), _planningOptions.ExpensiveQuantile) - _controllerOptions.BaseExpensivePriceMargin;

        var reserveCurveKwh = BuildReserveCurve(slots, cheapThreshold, expensiveThreshold);

        var plan = new List<PlannedAction>(slots.Count);
        var socKwh = PercentToKwh(currentState.BatterySocPercent);
        var minSocKwh = PercentToKwh(_controllerOptions.MinSocPercent);
        var maxSocKwh = PercentToKwh(_controllerOptions.MaxSocPercent);
        var slotDurationHours = _planningOptions.SlotDurationHours;

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var currentReserveKwh = Math.Min(maxSocKwh, Math.Max(minSocKwh, reserveCurveKwh[i]));
            var nextReserveKwh = i < slots.Count - 1
                ? Math.Min(maxSocKwh, Math.Max(minSocKwh, reserveCurveKwh[i + 1]))
                : Math.Min(maxSocKwh, Math.Max(minSocKwh, reserveCurveKwh[i]));

            var isCheap = slot.Price <= cheapThreshold && slot.Price <= _planningOptions.MaxGridChargePrice;
            var isExpensive = slot.Price >= expensiveThreshold;
            var hasPvSurplus = slot.ExcessPvWatts > 50;
            var shouldKeepPvHeadroom = _planningOptions.PreferPvOverGrid && GetUpcomingPvSurplusKwh(slots, i + 1, 24) > 0.75;

            BatteryAction action = BatteryAction.Hold;
            double targetPowerWatts = 0;
            double gridPowerWatts = slot.NetDemandWatts;
            string reason;

            var headroomKwh = Math.Max(0, maxSocKwh - socKwh);
            var availableAboveReserveKwh = Math.Max(0, socKwh - nextReserveKwh);

            if (hasPvSurplus && headroomKwh > 0.01)
            {
                var maxChargeByHeadroomWatts = (headroomKwh / slotDurationHours) * 1000.0 / Math.Max(0.01, _controllerOptions.ChargeEfficiency);
                var chargeWatts = Math.Min(_controllerOptions.MaxChargePowerWatts, Math.Min(slot.ExcessPvWatts, maxChargeByHeadroomWatts));
                if (chargeWatts >= _planningOptions.MinChargePowerWatts)
                {
                    action = BatteryAction.Charge;
                    targetPowerWatts = chargeWatts;
                    gridPowerWatts = Math.Max(0, slot.ForecastLoadWatts - slot.ForecastPvWatts + chargeWatts);
                    socKwh = Math.Min(maxSocKwh, socKwh + ((chargeWatts / 1000.0) * slotDurationHours * _controllerOptions.ChargeEfficiency));
                    reason = $"PV-Überschuss laden: {slot.ExcessPvWatts:F0}W Überschuss, Reserveziel {KwhToPercent(currentReserveKwh):F0}%";
                    plan.Add(ToAction(slot, action, targetPowerWatts, socKwh, gridPowerWatts, currentReserveKwh, reason));
                    continue;
                }
            }

            if (_planningOptions.EnableGridCharging
                && isCheap
                && headroomKwh > 0.01
                && (!shouldKeepPvHeadroom || GetUpcomingCheapSlots(slots, i + 1, 24, cheapThreshold) == 0))
            {
                var deficitToReserveKwh = Math.Max(0, nextReserveKwh - socKwh);
                var futureExpensiveNeedKwh = GetUpcomingExpensiveNeedKwh(slots, i + 1, 32, expensiveThreshold);
                var desiredChargeKwh = Math.Max(deficitToReserveKwh, futureExpensiveNeedKwh * 0.35);

                if (desiredChargeKwh > 0.01)
                {
                    var desiredChargeWatts = (desiredChargeKwh / slotDurationHours) * 1000.0;
                    var maxChargeByHeadroomWatts = (headroomKwh / slotDurationHours) * 1000.0 / Math.Max(0.01, _controllerOptions.ChargeEfficiency);
                    var chargeWatts = Math.Min(
                        _controllerOptions.MaxChargePowerWatts,
                        Math.Min(maxChargeByHeadroomWatts, desiredChargeWatts * _planningOptions.CheapChargeAggressiveness));

                    chargeWatts = Math.Max(_planningOptions.MinChargePowerWatts, chargeWatts);
                    chargeWatts = Math.Min(chargeWatts, _controllerOptions.MaxChargePowerWatts);

                    if (chargeWatts >= _planningOptions.MinChargePowerWatts)
                    {
                        action = BatteryAction.Charge;
                        targetPowerWatts = chargeWatts;
                        gridPowerWatts = Math.Max(0, slot.ForecastLoadWatts - slot.ForecastPvWatts + chargeWatts);
                        socKwh = Math.Min(maxSocKwh, socKwh + ((chargeWatts / 1000.0) * slotDurationHours * _controllerOptions.ChargeEfficiency));
                        reason = $"Günstig vorladen: Preis {slot.Price:F3}, Zielreserve {KwhToPercent(nextReserveKwh):F0}%, erwarteter teurer Bedarf {futureExpensiveNeedKwh:F1}kWh";
                        plan.Add(ToAction(slot, action, targetPowerWatts, socKwh, gridPowerWatts, currentReserveKwh, reason));
                        continue;
                    }
                }
            }

            if (isExpensive && slot.NetDemandWatts > 0 && availableAboveReserveKwh > 0.01)
            {
                var maxDischargeByReserveWatts = (availableAboveReserveKwh * _controllerOptions.DischargeEfficiency / slotDurationHours) * 1000.0;
                var dischargeWatts = Math.Min(
                    _controllerOptions.MaxDischargePowerWatts,
                    Math.Min(slot.NetDemandWatts * _planningOptions.ExpensiveDischargeAggressiveness, maxDischargeByReserveWatts));

                if (dischargeWatts >= _planningOptions.MinDischargePowerWatts)
                {
                    action = BatteryAction.Discharge;
                    targetPowerWatts = dischargeWatts;
                    var deliveredWatts = Math.Min(dischargeWatts, slot.NetDemandWatts);
                    gridPowerWatts = Math.Max(0, slot.ForecastLoadWatts - slot.ForecastPvWatts - deliveredWatts);
                    socKwh = Math.Max(minSocKwh, socKwh - ((deliveredWatts / 1000.0) * slotDurationHours / Math.Max(0.01, _controllerOptions.DischargeEfficiency)));
                    reason = $"Teures Viertel entladen: Preis {slot.Price:F3}, Nettobedarf {slot.NetDemandWatts:F0}W, Reserveziel {KwhToPercent(nextReserveKwh):F0}%";
                    plan.Add(ToAction(slot, action, targetPowerWatts, socKwh, gridPowerWatts, currentReserveKwh, reason));
                    continue;
                }
            }

            reason = shouldKeepPvHeadroom
                ? $"Hold für PV-Freiraum: kommende PV-Überschüsse erwartet, Reserveziel {KwhToPercent(currentReserveKwh):F0}%"
                : $"Hold: Preis {slot.Price:F3}, Nettobedarf {slot.NetDemandWatts:F0}W, Reserveziel {KwhToPercent(currentReserveKwh):F0}%";

            gridPowerWatts = Math.Max(0, slot.ForecastLoadWatts - slot.ForecastPvWatts);
            plan.Add(ToAction(slot, action, 0, socKwh, gridPowerWatts, currentReserveKwh, reason));
        }

        return plan;
    }

    private async Task<List<ForecastSlot>> BuildForecastSlotsAsync(
        IReadOnlyList<PricePoint> prices,
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken)
    {
        var start = FloorToResolution(referenceTime, _planningOptions.SlotResolutionMinutes);
        var slotPrices = prices
            .Where(x => x.StartsAt >= start)
            .OrderBy(x => x.StartsAt)
            .GroupBy(x => x.StartsAt)
            .Select(g => new PricePoint(g.Key, g.Average(x => x.TotalPricePerKwh)))
            .Take(_planningOptions.PlanHorizonSlots)
            .ToList();

        var result = new List<ForecastSlot>(slotPrices.Count);
        foreach (var price in slotPrices)
        {
            var load = await _consumptionForecastService.GetExpectedConsumptionWhAsync(price.StartsAt, cancellationToken);
            var pv = await _pvForecastService.GetExpectedPvWhAsync(price.StartsAt, cancellationToken);
            var netDemand = Math.Max(0, load - pv);
            var excessPv = Math.Max(0, pv - load);
            result.Add(new ForecastSlot(price.StartsAt, price.TotalPricePerKwh, load, pv, netDemand, excessPv));
        }

        return result;
    }

    private double[] BuildReserveCurve(IReadOnlyList<ForecastSlot> slots, decimal cheapThreshold, decimal expensiveThreshold)
    {
        var curve = new double[slots.Count];
        var minSocKwh = PercentToKwh(_controllerOptions.MinSocPercent);
        var maxSocKwh = PercentToKwh(_controllerOptions.MaxSocPercent);
        var reserveKwh = Math.Min(maxSocKwh, minSocKwh + _planningOptions.BaseReserveKwh);
        var slotDurationHours = _planningOptions.SlotDurationHours;

        for (var i = slots.Count - 1; i >= 0; i--)
        {
            var slot = slots[i];
            var expensiveNeedKwh = slot.Price >= expensiveThreshold ? (slot.NetDemandWatts / 1000.0) * slotDurationHours : 0;
            var eveningBias = IsEvening(slot.StartsAt) ? _planningOptions.EveningReserveKwh : 0;
            var morningBias = IsMorning(slot.StartsAt) ? _planningOptions.MorningReserveKwh : 0;
            var pvHeadroomPenalty = _planningOptions.PreferPvOverGrid && GetUpcomingPvSurplusKwh(slots, i + 1, 24) > _planningOptions.PVPrefillHeadroomKwh
                ? _planningOptions.PVPrefillHeadroomKwh
                : 0;

            reserveKwh = Math.Max(minSocKwh, reserveKwh + (expensiveNeedKwh * _planningOptions.ReserveAggressiveness));
            reserveKwh = Math.Max(Math.Max(reserveKwh, minSocKwh + eveningBias), minSocKwh + morningBias);
            reserveKwh = Math.Min(maxSocKwh, reserveKwh);
            reserveKwh = Math.Max(minSocKwh, reserveKwh - pvHeadroomPenalty * 0.15);
            curve[i] = reserveKwh;

            if (slot.Price <= cheapThreshold)
            {
                reserveKwh = Math.Max(minSocKwh, reserveKwh - (0.08 * slotDurationHours));
            }
        }

        return curve;
    }

    private int GetUpcomingCheapSlots(IReadOnlyList<ForecastSlot> slots, int startIndex, int count, decimal cheapThreshold)
    {
        return slots.Skip(startIndex).Take(count).Count(x => x.Price <= cheapThreshold);
    }

    private double GetUpcomingExpensiveNeedKwh(IReadOnlyList<ForecastSlot> slots, int startIndex, int count, decimal expensiveThreshold)
    {
        return slots.Skip(startIndex)
            .Take(count)
            .Where(x => x.Price >= expensiveThreshold)
            .Sum(x => (x.NetDemandWatts / 1000.0) * _planningOptions.SlotDurationHours);
    }

    private double GetUpcomingPvSurplusKwh(IReadOnlyList<ForecastSlot> slots, int startIndex, int count)
    {
        return slots.Skip(startIndex)
            .Take(count)
            .Sum(x => (x.ExcessPvWatts / 1000.0) * _planningOptions.SlotDurationHours);
    }

    private PlannedAction ToAction(ForecastSlot slot, BatteryAction action, double targetPowerWatts, double socKwh, double gridPowerWatts, double reserveKwh, string reason)
    {
        return new PlannedAction(
            slot.StartsAt,
            action,
            targetPowerWatts,
            slot.Price,
            KwhToPercent(socKwh),
            slot.ForecastLoadWatts,
            slot.ForecastPvWatts,
            gridPowerWatts,
            KwhToPercent(reserveKwh),
            reason);
    }

    private double PercentToKwh(double percent)
        => _controllerOptions.BatteryUsableCapacityKwh * (percent / 100.0);

    private double KwhToPercent(double kwh)
        => _controllerOptions.BatteryUsableCapacityKwh <= 0 ? 0 : (kwh / _controllerOptions.BatteryUsableCapacityKwh) * 100.0;

    private static decimal GetQuantile(IEnumerable<decimal> values, double quantile)
    {
        var list = values.OrderBy(x => x).ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Clamp(Math.Round((list.Count - 1) * quantile), 0, list.Count - 1);
        return list[index];
    }

    private static bool IsMorning(DateTimeOffset timestamp)
        => timestamp.Hour is >= 5 and < 9;

    private static bool IsEvening(DateTimeOffset timestamp)
        => timestamp.Hour is >= 17 and < 22;

    private static DateTimeOffset FloorToResolution(DateTimeOffset value, int resolutionMinutes)
    {
        var flooredMinute = (value.Minute / resolutionMinutes) * resolutionMinutes;
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, flooredMinute, 0, value.Offset);
    }
}
