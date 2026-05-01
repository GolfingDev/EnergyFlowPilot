namespace TibberVictronController.Business.Models;

/// <summary>
/// Provides the central default catalog for database seeding and future setting repair.
/// </summary>
public static class ControllerSettingDefaults
{
    public const string BatteryTotalCapacityKwhKey = "battery.totalCapacityKwh";
    public const string BatteryMinimumStateOfChargePercentKey = "battery.minimumStateOfChargePercent";
    public const string BatteryMaximumChargePowerWattsKey = "battery.maximumChargePowerWatts";
    public const string BatteryMaximumDischargePowerWattsKey = "battery.maximumDischargePowerWatts";
    public const string BatteryRoundTripEfficiencyPercentKey = "battery.roundTripEfficiencyPercent";
    public const string DecisionLogRetentionDaysKey = "decisionLog.retentionDays";
    public const string ForecastHorizonHoursKey = "forecast.horizonHours";
    public const string GridFeedInCompensationPricePerKwhKey = "gridFeedIn.compensationPricePerKwh";
    public const string TibberApiEndpointKey = "tibber.apiEndpoint";
    public const string TibberHomeSelectionKey = "tibber.homeSelection";
    public const string TibberAccessTokenKey = "tibber.accessToken";
    public const string MqttHostKey = "mqtt.host";
    public const string MqttPortKey = "mqtt.port";
    public const string MqttUsernameKey = "mqtt.username";
    public const string MqttPasswordKey = "mqtt.password";

    private static readonly ControllerSettingDefinition[] Definitions =
    {
        new(BatteryTotalCapacityKwhKey, "10", ControllerSettingSensitivity.Normal),
        new(BatteryMinimumStateOfChargePercentKey, "10", ControllerSettingSensitivity.Normal),
        new(BatteryMaximumChargePowerWattsKey, "3000", ControllerSettingSensitivity.Normal),
        new(BatteryMaximumDischargePowerWattsKey, "3000", ControllerSettingSensitivity.Normal),
        new(BatteryRoundTripEfficiencyPercentKey, "90", ControllerSettingSensitivity.Normal),
        new(DecisionLogRetentionDaysKey, "90", ControllerSettingSensitivity.Normal),
        new(ForecastHorizonHoursKey, "24", ControllerSettingSensitivity.Normal),
        new(GridFeedInCompensationPricePerKwhKey, "0.08", ControllerSettingSensitivity.Normal),
        new(TibberApiEndpointKey, "https://api.tibber.com/v1-beta/gql", ControllerSettingSensitivity.Normal),
        new(TibberHomeSelectionKey, "first", ControllerSettingSensitivity.Normal),
        new(TibberAccessTokenKey, null, ControllerSettingSensitivity.Sensitive),
        new(MqttHostKey, "localhost", ControllerSettingSensitivity.Normal),
        new(MqttPortKey, "1883", ControllerSettingSensitivity.Normal),
        new(MqttUsernameKey, null, ControllerSettingSensitivity.Sensitive),
        new(MqttPasswordKey, null, ControllerSettingSensitivity.Sensitive)
    };

    /// <summary>
    /// Gets all known setting definitions that must exist in the database.
    /// </summary>
    public static IReadOnlyList<ControllerSettingDefinition> GetDefinitions()
    {
        return Definitions;
    }

    /// <summary>
    /// Creates the default settings used when the database is created or repaired.
    /// </summary>
    public static IReadOnlyList<ControllerSetting> CreateDefaultSettings(DateTimeOffset updatedAtUtc)
    {
        if (updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Aktualisierungszeitpunkt fuer Default-Einstellungen muss in UTC angegeben sein.", nameof(updatedAtUtc));
        }

        return Definitions
            .Select(definition => definition.CreateSetting(updatedAtUtc))
            .ToArray();
    }
}
