using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weishaupt;

public sealed class DatabaseWeishauptHotWaterSettingsProvider(IControllerSettingStore store)
{
    public async Task<WeishauptHotWaterSettings> GetSettingsAsync(CancellationToken ct)
    {
        var values = (await store.GetAllSettingsAsync(ct)).ToDictionary(s => s.Key, s => s.Value);
        decimal Number(string key, decimal minimum, decimal maximum)
        {
            if (!values.TryGetValue(key, out var value) || !decimal.TryParse(value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
                number < minimum || number > maximum)
                throw new InvalidOperationException($"Weishaupt-Einstellung '{key}' muss zwischen {minimum} und {maximum} liegen.");
            return number;
        }
        int Integer(string key, int minimum, int maximum)
        {
            var number = Number(key, minimum, maximum);
            if (number != decimal.Truncate(number)) throw new InvalidOperationException($"Weishaupt-Einstellung '{key}' muss ganzzahlig sein.");
            return (int)number;
        }
        return new WeishauptHotWaterSettings
        {
            IsEnabled = values.TryGetValue(ControllerSettingDefaults.WeishauptHotWaterEnabledKey, out var enabled) && bool.TryParse(enabled, out var flag) && flag,
            PvSurplusWatts = Integer(ControllerSettingDefaults.WeishauptHotWaterPvSurplusKey, 500, 20000),
            MinimumPriceAdvantagePerKwh = Number(ControllerSettingDefaults.WeishauptHotWaterMinimumAdvantageKey, 0.01m, 1m),
            ExtraEnergyPercent = Number(ControllerSettingDefaults.WeishauptHotWaterExtraEnergyKey, 0m, 100m),
            DemandHourLocal = Integer(ControllerSettingDefaults.WeishauptHotWaterDemandHourKey, 0, 23),
            InitialHoldHours = Number(ControllerSettingDefaults.WeishauptHotWaterInitialHoldHoursKey, 1m, 12m),
            InitialChargeMinutes = Integer(ControllerSettingDefaults.WeishauptHotWaterInitialChargeMinutesKey, 15, 180)
        };
    }
}
