using System.Globalization;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>Validates control values before they can make the next worker configuration unreadable.</summary>
internal static class ControllerSettingValueValidation
{
    public static void Validate(string key, string? value)
    {
        // Cross-field relationships belong to the complete configuration/draft. The settings API
        // saves individual fields, so checking intermediate combinations here would prevent valid edits.
        (decimal Min, decimal Max, bool Integer)? rule = key switch
        {
            ControllerSettingDefaults.BatteryMaximumChargePowerWattsKey or
            ControllerSettingDefaults.BatteryMaximumDischargePowerWattsKey => (1m, int.MaxValue, true),
            ControllerSettingDefaults.BatteryTotalCapacityKwhKey => (0m, decimal.MaxValue, false),
            ControllerSettingDefaults.BatteryMinimumStateOfChargePercentKey => (0m, 100m, false),
            ControllerSettingDefaults.BatteryPlanningMinimumStateOfChargePercentKey or
            ControllerSettingDefaults.BatteryPlanningMaximumStateOfChargePercentKey or
            ControllerSettingDefaults.BatteryTargetEndStateOfChargePercentKey or
            ControllerSettingDefaults.BatteryTemporaryStateOfChargePercentKey => (0m, 100m, false),
            ControllerSettingDefaults.BatteryRoundTripEfficiencyPercentKey => (0m, 100m, false),
            ControllerSettingDefaults.DecisionMinimumSavingsPerKwhKey or
            ControllerSettingDefaults.BatteryWearCostPerKwhKey or
            ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey or
            ControllerSettingDefaults.WeishauptHotWaterCheapPriceKey => (0m, decimal.MaxValue, false),
            ControllerSettingDefaults.WeishauptHotWaterMinimumAdvantageKey => (.01m, 1m, false),
            ControllerSettingDefaults.WeishauptHotWaterExtraEnergyKey => (0m, 100m, false),
            ControllerSettingDefaults.WeishauptHotWaterDemandHourKey => (0m, 23m, true),
            ControllerSettingDefaults.WeishauptHotWaterPvSurplusKey => (500m, 20000m, true),
            ControllerSettingDefaults.WeishauptHotWaterInitialHoldHoursKey => (1m, 12m, false),
            ControllerSettingDefaults.WeishauptHotWaterInitialChargeMinutesKey => (15m, 180m, true),
            ControllerSettingDefaults.ForecastHorizonHoursKey => (1m, 72m, true),
            _ => null
        };

        if (rule is not { } bounds) return;
        // No thousands separators: e.g. "4000,5" must never silently become 40005.
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number) || number < bounds.Min || number > bounds.Max ||
            (bounds.Integer && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) ||
            (key is ControllerSettingDefaults.BatteryTotalCapacityKwhKey or ControllerSettingDefaults.BatteryRoundTripEfficiencyPercentKey && number == 0m) ||
            (key == ControllerSettingDefaults.BatteryMinimumStateOfChargePercentKey && number == 100m))
        {
            throw new ArgumentException($"Die Einstellung '{key}' braucht {(bounds.Integer ? "eine ganze Zahl" : "eine Dezimalzahl")} im gueltigen Wertebereich ({bounds.Min} bis {bounds.Max}).", nameof(value));
        }
    }
}
