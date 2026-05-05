using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Battery;

/// <summary>
/// Provides temporary live site telemetry from persisted settings until real Victron telemetry is connected.
/// </summary>
public sealed class ConfiguredCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly IUtcClock utcClock;

    public ConfiguredCurrentSiteTelemetryProvider(
        IControllerSettingStore controllerSettingStore,
        IUtcClock utcClock)
    {
        this.controllerSettingStore = controllerSettingStore;
        this.utcClock = utcClock;
    }

    public async Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        var gridImportWatts = await GetRequiredIntegerSettingAsync(
            ControllerSettingDefaults.TelemetryTemporaryGridImportWattsKey,
            "Der temporaere Live-Netzbezug ist nicht konfiguriert.",
            "Der temporaere Live-Netzbezug muss als ganze Zahl konfiguriert sein.",
            cancellationToken);
        var pvProductionWatts = await GetRequiredIntegerSettingAsync(
            ControllerSettingDefaults.TelemetryTemporaryPvProductionWattsKey,
            "Die temporaere Live-PV-Leistung ist nicht konfiguriert.",
            "Die temporaere Live-PV-Leistung muss als ganze Zahl konfiguriert sein.",
            cancellationToken);

        return new CurrentSiteTelemetry(
            gridImportWatts,
            pvProductionWatts,
            utcClock.UtcNow);
    }

    private async Task<int> GetRequiredIntegerSettingAsync(
        string settingKey,
        string missingMessage,
        string invalidMessage,
        CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(settingKey, cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException(missingMessage);
        }

        if (!int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(invalidMessage);
        }

        return value;
    }
}
