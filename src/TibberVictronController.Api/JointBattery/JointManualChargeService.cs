using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

public sealed record JointManualChargeDto(bool IsActive, decimal TargetKwh, decimal ChargedKwh, int MaximumPowerWatts,
    DateTimeOffset? ExpiresAtUtc, string? LastResult);

public sealed record JointManualChargeRequestDto(decimal TargetKwh, decimal? MaximumPowerKw);

/// <summary>Validates and reports manual joint charges; the execution loop carries them out.</summary>
public sealed class JointManualChargeService(IControllerSettingStore settings, IUtcClock clock, JointBatteryExecutionState state)
{
    private JointManualChargeStore Store => new(settings, clock);

    /// <returns>Null on success, otherwise a German reason why the charge was not started.</returns>
    public async Task<string?> TryStartAsync(JointManualChargeRequestDto request, CancellationToken token)
    {
        async Task<bool> Flag(string key, bool fallback) => bool.TryParse((await settings.GetSettingAsync(key, token))?.Value, out var value) ? value : fallback;
        if (!await Flag(ControllerSettingDefaults.JointBatteryExecutionEnabledKey, false) || await Flag(ControllerSettingDefaults.VictronDryRunKey, true))
            return "Die manuelle Ladung beider Akkus benoetigt die eingeschaltete gemeinsame Steuerung ohne globalen Shadow-Modus.";
        if (request.TargetKwh is <= 0 or > 100)
            return "Die Lademenge muss groesser 0 und hoechstens 100 kWh sein.";
        if (request.MaximumPowerKw is { } kw && kw is <= 0 or > 50)
            return "Die Hoechstleistung muss leer oder groesser 0 und hoechstens 50 kW sein.";

        var maximumPowerWatts = request.MaximumPowerKw is { } limit ? (int)Math.Round(limit * 1000m, MidpointRounding.AwayFromZero) : 0;
        state.ManualChargeResult = null;
        await Store.StartAsync(Math.Round(request.TargetKwh, 2), maximumPowerWatts, token);
        return null;
    }

    public async Task StopAsync(CancellationToken token)
    {
        if (await Store.ReadAsync(token) is null) return;
        await Store.StopAsync(token);
        state.ManualChargeResult = "Manuelle Ladung im Dashboard beendet.";
    }

    public async Task<JointManualChargeDto> GetStatusAsync(CancellationToken token)
    {
        var request = await Store.ReadAsync(token);
        if (request is null) return new(false, 0, 0, 0, null, state.ManualChargeResult);
        var live = state.LastLive;
        var charged = request.BaselineKwh is { } baseline && live is not null
            ? Math.Max(0, JointManualCharge.StoredEnergyKwh(live.Batteries) - baseline) : 0;
        return new(true, request.TargetKwh, Math.Round(charged, 2), request.MaximumPowerWatts, request.ExpiresAtUtc, null);
    }
}
