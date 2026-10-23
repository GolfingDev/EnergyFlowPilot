using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>
/// Decides whether the E3/DC's reported effective power limits cap our requests. The device reported 97 W at its
/// reserve although its system limit is 3 kW; by default we request more and use what it actually grants
/// (the execution coordinator reads the granted power back). Planning and execution share this rule.
/// </summary>
internal static class E3dcLimitPolicy
{
    /// <returns>Charge/discharge caps for the planner and limiter; null means only the E3/DC system limit applies.</returns>
    public static async Task<(int? Charge, int? Discharge)> ActiveLimitsAsync(IControllerSettingStore settings,
        E3dcLocalTelemetry local, CancellationToken token)
    {
        var respect = bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.E3dcRespectReportedLimitsKey, token))?.Value, out var value) && value;
        return respect ? (local.EffectiveChargeLimitWatts, local.EffectiveDischargeLimitWatts) : (null, null);
    }
}
