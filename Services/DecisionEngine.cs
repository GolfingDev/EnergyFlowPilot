using Microsoft.Extensions.Options;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public class DecisionEngine
{
    private readonly ControllerOptions _options;
    private readonly IConsumptionForecastService _consumptionForecastService;

    public DecisionEngine(
        IOptions<ControllerOptions> options,
        IConsumptionForecastService consumptionForecastService)
    {
        _options = options.Value;
        _consumptionForecastService = consumptionForecastService;
    }

    public Task<Decision> BuildDecisionAsync(
        EnergyState state,
        IReadOnlyList<PricePoint> prices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return BuildDecisionInternalAsync(state, prices, now, cancellationToken);
    }

    public Task<Decision> BuildDecisionForPointAsync(
        EnergyState state,
        IReadOnlyList<PricePoint> prices,
        DateTimeOffset pointTime,
        CancellationToken cancellationToken = default)
    {
        return BuildDecisionInternalAsync(state, prices, pointTime, cancellationToken);
    }

    private async Task<Decision> BuildDecisionInternalAsync(
        EnergyState state,
        IReadOnlyList<PricePoint> prices,
        DateTimeOffset decisionTime,
        CancellationToken cancellationToken)
    {
        var currentPrice = prices
            .FirstOrDefault(p => p.StartsAt <= decisionTime && p.StartsAt.AddHours(1) > decisionTime)
            ?? prices.OrderBy(p => Math.Abs((p.StartsAt - decisionTime).TotalMinutes)).First();

        var next12h = prices
            .Where(p => p.StartsAt >= decisionTime)
            .Take(12)
            .ToList();

        var avg12h = next12h.Any() ? next12h.Average(x => x.TotalPricePerKwh) : currentPrice.TotalPricePerKwh;
        var min12h = next12h.Any() ? next12h.Min(x => x.TotalPricePerKwh) : currentPrice.TotalPricePerKwh;

        var expectedConsumptionNextHourWh = await _consumptionForecastService
            .GetExpectedConsumptionWhAsync(decisionTime, cancellationToken);

        var chargeMargin = _options.BaseCheapPriceTolerance;
        var dischargeMargin = _options.BaseExpensivePriceMargin;

        if (state.BatterySocPercent > 90)
        {
            dischargeMargin -= 0.03m;
        }
        else if (state.BatterySocPercent < 45)
        {
            dischargeMargin += 0.04m;
        }

        if (state.BatterySocPercent < 35)
        {
            chargeMargin += 0.02m;
        }
        else if (state.BatterySocPercent > 85)
        {
            chargeMargin -= 0.01m;
        }

        var isCheap = currentPrice.TotalPricePerKwh <= min12h + chargeMargin;
        var isExpensive = currentPrice.TotalPricePerKwh >= avg12h + dischargeMargin;

        var batteryLow = state.BatterySocPercent <= _options.MinSocPercent;
        var batteryHighEnough = state.BatterySocPercent >= _options.DischargeSocPercent;
        var batteryCanCharge = state.BatterySocPercent < _options.MaxSocPercent;

        if (batteryLow)
        {
            return new Decision(
                BatteryAction.Hold,
                0,
                currentPrice.TotalPricePerKwh,
                $"Akku schützen: SoC {state.BatterySocPercent:F1}% <= Minimum {_options.MinSocPercent:F1}%");
        }

        if (isCheap && batteryCanCharge)
        {
            var target = Math.Min(_options.MaxChargePowerWatts, Math.Max(500, expectedConsumptionNextHourWh));
            return new Decision(
                BatteryAction.Charge,
                target,
                currentPrice.TotalPricePerKwh,
                $"Günstiger Preis ({currentPrice.TotalPricePerKwh:F3}) bei SoC {state.BatterySocPercent:F1}%");
        }

        if (isExpensive && batteryHighEnough)
        {
            var target = Math.Min(
                _options.MaxDischargePowerWatts,
                Math.Max(300, state.HouseConsumptionWatts - Math.Max(0, state.PvPowerWatts)));

            return new Decision(
                BatteryAction.Discharge,
                target,
                currentPrice.TotalPricePerKwh,
                $"Teurer Preis ({currentPrice.TotalPricePerKwh:F3}) bei SoC {state.BatterySocPercent:F1}%");
        }

        return new Decision(
            BatteryAction.Hold,
            0,
            currentPrice.TotalPricePerKwh,
            $"Hold: Preis {currentPrice.TotalPricePerKwh:F3}, Ø12h {avg12h:F3}, Verbrauchsprognose {expectedConsumptionNextHourWh:F0}Wh");
    }
}
