using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Battery;

/// <summary>
/// Loads Battery Decision Engine battery configuration from persisted controller settings.
/// </summary>
public sealed class DatabaseBatteryConfigurationProvider : IBatteryConfigurationProvider
{
    private readonly IControllerSettingStore controllerSettingStore;

    public DatabaseBatteryConfigurationProvider(IControllerSettingStore controllerSettingStore)
    {
        this.controllerSettingStore = controllerSettingStore;
    }

    /// <summary>
    /// Reads all required battery settings and lets the domain model validate physical boundaries.
    /// </summary>
    public async Task<BatteryConfiguration> GetBatteryConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var totalCapacityKwh = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.BatteryTotalCapacityKwhKey,
            "Die Batteriekapazitaet ist nicht konfiguriert.",
            "Die Batteriekapazitaet muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var minimumStateOfChargePercent = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.BatteryMinimumStateOfChargePercentKey,
            "Der minimale Akkuladestand ist nicht konfiguriert.",
            "Der minimale Akkuladestand muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var maximumChargePowerWatts = await GetRequiredIntegerSettingAsync(
            ControllerSettingDefaults.BatteryMaximumChargePowerWattsKey,
            "Die maximale Ladeleistung ist nicht konfiguriert.",
            "Die maximale Ladeleistung muss als ganze Zahl konfiguriert sein.",
            cancellationToken);
        var maximumDischargePowerWatts = await GetRequiredIntegerSettingAsync(
            ControllerSettingDefaults.BatteryMaximumDischargePowerWattsKey,
            "Die maximale Entladeleistung ist nicht konfiguriert.",
            "Die maximale Entladeleistung muss als ganze Zahl konfiguriert sein.",
            cancellationToken);
        var roundTripEfficiencyPercent = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.BatteryRoundTripEfficiencyPercentKey,
            "Der Batterie-Wirkungsgrad ist nicht konfiguriert.",
            "Der Batterie-Wirkungsgrad muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var targetEndStateOfChargePercent = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.BatteryTargetEndStateOfChargePercentKey,
            "Die Ziel-Endreserve ist nicht konfiguriert.",
            "Die Ziel-Endreserve muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var planningMinimumStateOfChargePercent = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.BatteryPlanningMinimumStateOfChargePercentKey,
            "Die Planungsreserve ist nicht konfiguriert.",
            "Die Planungsreserve muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var planningMaximumStateOfChargePercent = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.BatteryPlanningMaximumStateOfChargePercentKey,
            "Das Planungs-Maximum ist nicht konfiguriert.",
            "Das Planungs-Maximum muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);

        return new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = totalCapacityKwh,
            MinimumStateOfChargePercent = minimumStateOfChargePercent,
            MaximumChargePowerWatts = maximumChargePowerWatts,
            MaximumDischargePowerWatts = maximumDischargePowerWatts,
            RoundTripEfficiencyPercent = roundTripEfficiencyPercent,
            TargetEndStateOfChargePercent = targetEndStateOfChargePercent,
            PlanningMinimumStateOfChargePercent = planningMinimumStateOfChargePercent,
            PlanningMaximumStateOfChargePercent = planningMaximumStateOfChargePercent
        });
    }

    private async Task<decimal> GetRequiredDecimalSettingAsync(
        string settingKey,
        string missingMessage,
        string invalidMessage,
        CancellationToken cancellationToken)
    {
        var settingValue = await GetRequiredSettingValueAsync(settingKey, missingMessage, cancellationToken);

        if (!decimal.TryParse(settingValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(invalidMessage);
        }

        return value;
    }

    private async Task<int> GetRequiredIntegerSettingAsync(
        string settingKey,
        string missingMessage,
        string invalidMessage,
        CancellationToken cancellationToken)
    {
        var settingValue = await GetRequiredSettingValueAsync(settingKey, missingMessage, cancellationToken);

        if (!int.TryParse(settingValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(invalidMessage);
        }

        return value;
    }

    private async Task<string> GetRequiredSettingValueAsync(
        string settingKey,
        string missingMessage,
        CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(settingKey, cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException(missingMessage);
        }

        return setting.Value!;
    }
}
