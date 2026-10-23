using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Projects joint execution into existing Victron dashboard contracts without running a competing decision engine.</summary>
public sealed class JointAwareBatteryDecisionService(IServiceProvider services) : ICurrentBatteryDecisionService
{
    public async Task<CurrentBatteryDecisionResult> CalculateCurrentDecisionAsync(CancellationToken cancellationToken = default)
    {
        var store = services.GetRequiredService<IControllerSettingStore>();
        if (!await JointBatteryOwnership.BlocksLegacyAsync(store, cancellationToken))
            return await services.GetRequiredService<CurrentBatteryDecisionService>().CalculateCurrentDecisionAsync(cancellationToken);
        var now = services.GetRequiredService<IUtcClock>().UtcNow;
        var execution = services.GetRequiredService<JointBatteryExecutionState>().Current;
        var mqtt = services.GetRequiredService<MqttTelemetrySnapshotStore>().GetSnapshot();
        if (mqtt.BatterySocPercent is null || mqtt.BatterySocMeasuredAtUtc is null || mqtt.GridPowerWatts is null || mqtt.GridPowerMeasuredAtUtc is null)
            throw new InvalidOperationException("Aktuelle Messwerte fuer die gemeinsame Akkusteuerung fehlen.");
        var active = execution.Status == "Active" && now - execution.UpdatedAtUtc < TimeSpan.FromSeconds(5) && execution.ValidUntilUtc > now;
        var target = active ? execution.VictronTargetWatts : 0;
        var slot = services.GetRequiredService<JointBatteryRuntimeState>().Current.Plan?.Slots.FirstOrDefault(s => s.TimeSlot.StartsAtUtc <= now && s.TimeSlot.EndsAtUtc > now);
        var decision = new CurrentBatteryDecision(new(target > 0 ? BatteryDecisionState.Charge : target < 0 ? BatteryDecisionState.Discharge : BatteryDecisionState.Idle,
            target > 0 ? slot?.ChargeSource ?? BatteryChargeSource.Grid : null), Math.Abs(target));
        var summary = JsonSerializer.Serialize(new { ControlSource = "JointBattery", Execution = execution });
        return new(now, now, now.AddSeconds(5), decision, new(mqtt.BatterySocPercent.Value, mqtt.BatterySocMeasuredAtUtc.Value),
            new((int)mqtt.GridPowerWatts.Value, 0, mqtt.GridPowerMeasuredAtUtc.Value, (int?)mqtt.BatteryPowerWatts), slot?.PricePerKwh, slot?.Currency,
            [new("JointBattery." + execution.Status, execution.Message)], summary);
    }
}
