using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

/// <summary>Reads the base state of charge question of the wallbox's running charging session.</summary>
public sealed class EvBaseChargeChoiceService(
    IEvChargerStatusSource charger,
    IEvChargingSessionRepository sessions,
    ICarStatusSource car,
    IControllerSettingStore settings)
{
    public async Task<EvBaseChargeChoiceState> GetAsync(CancellationToken cancellationToken)
    {
        async Task<double> Number(string key) => double.TryParse((await settings.GetSettingAsync(key, cancellationToken))?.Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var baseSoc = await Number(ControllerSettingDefaults.EvCarBaseSocKey);
        var capacity = await Number(ControllerSettingDefaults.EvCarUsableCapacityKwhKey);
        var wallbox = charger.Current;
        var session = wallbox is { IsConnected: true, ActiveTransactionId: { } id } ? await sessions.GetAsync(id, cancellationToken) : null;
        return EvBaseChargeChoice.Resolve(session, car.Car.StateOfChargeEstimatePercent(capacity), baseSoc);
    }
}
