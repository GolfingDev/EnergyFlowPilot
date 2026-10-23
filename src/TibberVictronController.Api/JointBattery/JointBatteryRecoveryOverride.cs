using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>
/// Operator escape hatch for a recovery obligation that cannot be released automatically, e.g. when the E3/DC
/// stays unreachable. Without it the persisted obligation would block single-battery control and the very
/// device settings needed to repair access, with no way out except editing the database.
/// </summary>
public sealed class JointBatteryRecoveryOverride(IControllerSettingStore settings, VictronSetpointRefreshState ownership,
    JointBatteryExecutionState state, IUtcClock clock, IOperationalEventRepository events,
    ILogger<JointBatteryRecoveryOverride> logger, DecisionCalculationTrigger? calculationTrigger = null)
{
    /// <returns>Null on success, otherwise a German reason why the confirmation was rejected.</returns>
    public async Task<string?> ConfirmAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        async Task<bool> Flag(string key) => bool.TryParse((await settings.GetSettingAsync(key, timeout.Token))?.Value, out var value) && value;

        // Same gate as the execution loop: no tick can claim or release ownership while the flags are changed.
        using var lease = await ownership.AcquirePublicationAsync(timeout.Token);
        if (await Flag(ControllerSettingDefaults.JointBatteryExecutionEnabledKey))
            return "Zuerst die gemeinsame Steuerung deaktivieren. Erst danach kann die Ruecknahme manuell bestaetigt werden.";
        if (!await Flag(ControllerSettingDefaults.JointBatteryRecoveryPendingKey) && !ownership.JointControlActive)
            return "Es ist keine gemeinsame Ruecknahme ausstehend.";

        await settings.SaveSettingAsync(new(ControllerSettingDefaults.JointBatteryRecoveryPendingKey, "false",
            ControllerSettingSensitivity.Normal, clock.UtcNow), timeout.Token);
        ownership.JointControlActive = false;
        state.Direction = 0; state.NeutralSamples = 0; state.SettlingSinceUtc = null; state.BlockedUntilUtc = null;
        // E3/DC drops an unrefreshed power command after at most 30 s, so it returns to automatic on its own.
        const string message = "Ruecknahme manuell bestaetigt. Einzelakku-Steuerung ist wieder freigegeben; " +
            "E3/DC kehrt ohne erneuerten Befehl spaetestens nach 30 Sekunden selbst in den Automatikbetrieb zurueck.";
        state.Set(new("Disabled", message, clock.UtcNow, false));
        logger.LogWarning("Gemeinsame Ruecknahme wurde manuell bestaetigt.");
        await events.SaveEventAsync(new OperationalEvent(Guid.NewGuid(), clock.UtcNow, "JointBatteryRecoveryOverride", "Warning",
            message, "{}"), timeout.Token);
        calculationTrigger?.Signal();
        return null;
    }
}
