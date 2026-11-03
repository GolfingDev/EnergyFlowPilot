using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>
/// Decides whether the E3/DC's reported effective power limits cap our requests. The device reported 97 W at its
/// reserve although its system limit is 3 kW; by default we request more and use what it actually grants
/// (the execution coordinator reads the granted power back). Planning and execution share this rule.
/// Own limits from the settings lower the requests further; they are never written to the device.
/// </summary>
internal static class E3dcLimitPolicy
{
    /// <returns>Charge/discharge caps for the planner and limiter; null means only the E3/DC system limit applies.</returns>
    public static async Task<(int? Charge, int? Discharge)> ActiveLimitsAsync(IControllerSettingStore settings,
        E3dcLocalTelemetry local, CancellationToken token)
    {
        var respect = bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.E3dcRespectReportedLimitsKey, token))?.Value, out var value) && value;
        var ownCharge = await ReadWattsAsync(settings, ControllerSettingDefaults.E3dcMaximumChargePowerKey, token);
        var ownDischarge = await ReadWattsAsync(settings, ControllerSettingDefaults.E3dcMaximumDischargePowerKey, token);
        return (E3dcPowerCaps.Combine(respect ? local.EffectiveChargeLimitWatts : null, ownCharge),
            E3dcPowerCaps.Combine(respect ? local.EffectiveDischargeLimitWatts : null, ownDischarge));
    }

    // An unreadable value counts as "no own limit": the device and system limits still apply in full.
    private static async Task<int> ReadWattsAsync(IControllerSettingStore settings, string key, CancellationToken token)
    {
        var raw = (await settings.GetSettingAsync(key, token))?.Value;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var watts) ? watts : 0;
    }
}
