using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>
/// Persists a manual joint charge in the settings store so it survives restarts. The settings API refuses these keys;
/// only this class (dashboard endpoints and the execution loop) changes them.
/// </summary>
public sealed class JointManualChargeStore(IControllerSettingStore settings, IUtcClock clock)
{
    // Upper bound for an unattended manual charge; a forgotten request must not keep charging indefinitely.
    public static readonly TimeSpan SafetyDuration = TimeSpan.FromHours(6);
    private static readonly DateTimeOffset Inactive = DateTimeOffset.UnixEpoch;

    public async Task<JointManualChargeRequest?> ReadAsync(CancellationToken token)
    {
        async Task<string?> Value(string key) => (await settings.GetSettingAsync(key, token))?.Value;
        if (!decimal.TryParse(await Value(ControllerSettingDefaults.JointManualChargeTargetKwhKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var target) || target <= 0)
            return null;
        if (!DateTimeOffset.TryParse(await Value(ControllerSettingDefaults.JointManualChargeExpiresAtUtcKey), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires) || expires <= Inactive)
            return null;
        var power = int.TryParse(await Value(ControllerSettingDefaults.JointManualChargeMaximumPowerWattsKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var watts)
            ? Math.Max(0, watts) : 0;
        decimal? baseline = decimal.TryParse(await Value(ControllerSettingDefaults.JointManualChargeBaselineKwhKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var kwh)
            ? kwh : null;
        return new(target, power, expires, baseline);
    }

    public async Task<JointManualChargeRequest> StartAsync(decimal targetKwh, int maximumPowerWatts, CancellationToken token)
    {
        var request = new JointManualChargeRequest(targetKwh, maximumPowerWatts, clock.UtcNow + SafetyDuration, null);
        // Baseline first: a stale baseline from an earlier charge must never be combined with the new target.
        await SaveAsync(ControllerSettingDefaults.JointManualChargeBaselineKwhKey, ControllerSettingDefaults.JointManualChargeNoBaseline, token);
        await SaveAsync(ControllerSettingDefaults.JointManualChargeMaximumPowerWattsKey, maximumPowerWatts.ToString(CultureInfo.InvariantCulture), token);
        await SaveAsync(ControllerSettingDefaults.JointManualChargeExpiresAtUtcKey, request.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture), token);
        await SaveAsync(ControllerSettingDefaults.JointManualChargeTargetKwhKey, targetKwh.ToString(CultureInfo.InvariantCulture), token);
        return request;
    }

    public Task SaveBaselineAsync(decimal baselineKwh, CancellationToken token) =>
        SaveAsync(ControllerSettingDefaults.JointManualChargeBaselineKwhKey, baselineKwh.ToString(CultureInfo.InvariantCulture), token);

    public async Task StopAsync(CancellationToken token)
    {
        // Target first: once it is 0 the request is inactive even if a later write fails.
        await SaveAsync(ControllerSettingDefaults.JointManualChargeTargetKwhKey, "0", token);
        await SaveAsync(ControllerSettingDefaults.JointManualChargeExpiresAtUtcKey, Inactive.ToString("O", CultureInfo.InvariantCulture), token);
        await SaveAsync(ControllerSettingDefaults.JointManualChargeBaselineKwhKey, ControllerSettingDefaults.JointManualChargeNoBaseline, token);
    }

    private Task SaveAsync(string key, string? value, CancellationToken token) =>
        settings.SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Normal, clock.UtcNow), token);
}
