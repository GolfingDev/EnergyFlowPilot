using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Battery;

/// <summary>
/// Provides a temporary persisted battery state until real Victron telemetry is connected.
/// </summary>
public sealed class ConfiguredBatteryStateProvider : IBatteryStateProvider
{
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly IUtcClock utcClock;

    public ConfiguredBatteryStateProvider(
        IControllerSettingStore controllerSettingStore,
        IUtcClock utcClock)
    {
        this.controllerSettingStore = controllerSettingStore;
        this.utcClock = utcClock;
    }

    /// <summary>
    /// Reads the configured temporary state of charge and timestamps it with the current UTC time.
    /// </summary>
    public async Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        var setting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.BatteryTemporaryStateOfChargePercentKey,
            cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException("Der temporaere Akkuladestand ist nicht konfiguriert.");
        }

        if (!decimal.TryParse(setting.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var stateOfChargePercent))
        {
            throw new InvalidOperationException("Der temporaere Akkuladestand muss als Dezimalzahl konfiguriert sein.");
        }

        return new BatteryState(stateOfChargePercent, utcClock.UtcNow);
    }
}
