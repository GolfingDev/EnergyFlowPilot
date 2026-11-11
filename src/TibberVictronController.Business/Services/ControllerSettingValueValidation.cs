using System.Globalization;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>Validates control values before they can make the next worker configuration unreadable.</summary>
internal static class ControllerSettingValueValidation
{
    public static void Validate(string key, string? value)
    {
        if (key == ControllerSettingDefaults.EnergyChartsSurchargeKey && value is null) return;
        if (key == ControllerSettingDefaults.EnergyChartsZoneKey && (string.IsNullOrWhiteSpace(value) || value.Length > 16 ||
            value.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c != '-')))
            throw new ArgumentException("Die Gebotszone muss ein gueltiger Code wie DE-LU sein.");
        if (key == ControllerSettingDefaults.ElectricitySupplierNameKey && value?.Length > 120)
            throw new ArgumentException("Der Anbietername darf hoechstens 120 Zeichen enthalten.");
        if (key == ControllerSettingDefaults.TariffComparisonOffersKey)
            DynamicTariffComparisonCalculator.ParseOffers(value);
        if (key is ControllerSettingDefaults.E3dcRscpEnabledKey or ControllerSettingDefaults.JointBatteryEnabledKey or ControllerSettingDefaults.JointBatteryExecutionEnabledKey
                or ControllerSettingDefaults.E3dcRespectReportedLimitsKey &&
            !bool.TryParse(value, out _))
            throw new ArgumentException("Der Schalter fuer lokalen E3/DC-Zugriff oder gemeinsame Vorschau muss true oder false sein.");
        if (key == ControllerSettingDefaults.DecisionLogCleanupEnabledKey && !bool.TryParse(value, out _))
            throw new ArgumentException("Der Schalter fuer das Aufraeumen des Entscheidungslogs muss true oder false sein.");
        if (key == ControllerSettingDefaults.EvOcppEnabledKey && !bool.TryParse(value, out _))
            throw new ArgumentException("Der Schalter fuer die Wallbox-Anbindung muss true oder false sein.");
        // The id becomes part of the WebSocket URL; restricting it keeps logs and URLs unambiguous.
        if (key == ControllerSettingDefaults.EvOcppChargePointIdKey && (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))))
            throw new ArgumentException("Die Ladepunkt-ID darf nur Buchstaben, Ziffern sowie - _ . : enthalten und hoechstens 64 Zeichen lang sein.");
        if (key == ControllerSettingDefaults.BmwCarDataClientIdKey && (string.IsNullOrWhiteSpace(value) || value.Length > 100 ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            throw new ArgumentException("Die CarData-Client-ID darf nur Buchstaben, Ziffern, - und _ enthalten.");
        if (key == ControllerSettingDefaults.BmwCarDataVinKey && value != ControllerSettingDefaults.BmwCarDataAutoVin &&
            (value is null || value.Length != 17 || !value.All(char.IsAsciiLetterOrDigit)))
            throw new ArgumentException("Die Fahrgestellnummer (VIN) muss 17 Zeichen lang sein oder 'auto' lauten.");
        if (key is ControllerSettingDefaults.EvCarUseLearnedConsumptionKey or ControllerSettingDefaults.PvForecastLearnedCorrectionEnabledKey &&
            !bool.TryParse(value, out _))
            throw new ArgumentException("Der Schalter muss true oder false sein.");
        if (key == ControllerSettingDefaults.EvChargingControlEnabledKey && !bool.TryParse(value, out _))
            throw new ArgumentException("Der Schalter fuer die automatische Ladesteuerung muss true oder false sein.");
        if (key == ControllerSettingDefaults.EvCalendarEnabledKey && !bool.TryParse(value, out _))
            throw new ArgumentException("Der Schalter fuer den Kalender muss true oder false sein.");
        if (key == ControllerSettingDefaults.EvCalendarMailboxKey && value != ControllerSettingDefaults.EvCalendarMailboxNotConfigured &&
            (value is null || value.Length > 254 || value.Count(c => c == '@') != 1 || value.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Das Postfach muss eine E-Mail-Adresse aus dem Microsoft-365-Mandanten sein.");
        if (key == ControllerSettingDefaults.E3dcRscpKeyKey && value is not null && System.Text.Encoding.UTF8.GetByteCount(value) > 32)
            throw new ArgumentException("Der RSCP-Schluessel darf maximal 32 UTF-8-Bytes enthalten.");
        // Cross-field relationships belong to the complete configuration/draft. The settings API
        // saves individual fields, so checking intermediate combinations here would prevent valid edits.
        (decimal Min, decimal Max, bool Integer)? rule = key switch
        {
            ControllerSettingDefaults.EnergyChartsSurchargeKey => (0m, 5m, false),
            ControllerSettingDefaults.EnergyChartsVatKey => (0m, 100m, false),
            ControllerSettingDefaults.E3dcRscpPortKey => (1m, 65535m, true),
            ControllerSettingDefaults.JointBatteryGridLimitKey => (0m, 100000m, true),
            ControllerSettingDefaults.E3dcMaximumChargePowerKey or ControllerSettingDefaults.E3dcMaximumDischargePowerKey => (0m, 100000m, true),
            ControllerSettingDefaults.JointBatteryPhase1LimitKey or ControllerSettingDefaults.JointBatteryPhase2LimitKey or
            ControllerSettingDefaults.JointBatteryPhase3LimitKey or ControllerSettingDefaults.JointBatteryVictronInputLimitKey => (0m, 100000m, true),
            ControllerSettingDefaults.E3dcPlanningMaximumSocKey or ControllerSettingDefaults.E3dcEfficiencyKey => (1m, 100m, false),
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
            ControllerSettingDefaults.EvCarUsableCapacityKwhKey => (10m, 200m, false),
            ControllerSettingDefaults.EvCarConsumptionKey => (5m, 60m, false),
            ControllerSettingDefaults.EvCarMinimumSocKey => (0m, 80m, false),
            ControllerSettingDefaults.EvCarBaseSocKey => (0m, 100m, false),
            ControllerSettingDefaults.EvTripWinterSurchargeKey => (0m, 5m, false),
            ControllerSettingDefaults.EvChargingCheapPercentileKey => (0m, 100m, false),
            ControllerSettingDefaults.EvChargingPvGridAllowanceKey => (0m, 4140m, true),
            ControllerSettingDefaults.EvTripMarginKey => (0m, 100m, false),
            ControllerSettingDefaults.EvWallboxMaximumPowerKey => (1400m, 22000m, true),
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
