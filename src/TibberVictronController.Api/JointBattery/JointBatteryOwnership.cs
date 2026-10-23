using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

public static class JointBatteryOwnership
{
    // The persisted flags block legacy writers immediately after restart, before the joint worker has run.
    public static async Task<bool> BlocksLegacyAsync(IControllerSettingStore? settings, CancellationToken token)
    {
        if (settings is null) return false;
        foreach (var key in new[] { ControllerSettingDefaults.JointBatteryExecutionEnabledKey, ControllerSettingDefaults.JointBatteryRecoveryPendingKey })
            if (bool.TryParse((await settings.GetSettingAsync(key, token))?.Value, out var active) && active) return true;
        return false;
    }
}
