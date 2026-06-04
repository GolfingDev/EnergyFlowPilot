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
    public const string BatteryTargetEndStateOfChargePercentKey = "battery.targetEndStateOfChargePercent";
    public const string BatteryPlanningMinimumStateOfChargePercentKey = "battery.planningMinimumStateOfChargePercent";
    public const string BatteryPlanningMaximumStateOfChargePercentKey = "battery.planningMaximumStateOfChargePercent";
    public const string BatteryTemporaryStateOfChargePercentKey = "battery.temporaryStateOfChargePercent";
    public const string TelemetryTemporaryGridImportWattsKey = "telemetry.temporaryGridImportWatts";
    public const string TelemetryTemporaryPvProductionWattsKey = "telemetry.temporaryPvProductionWatts";
    public const string TelemetryGridPowerDeadbandWattsKey = "telemetry.gridPowerDeadbandWatts";
    public const string TelemetryGridImportSourceKey = "telemetry.sources.gridImportWatts";
    public const string TelemetryPvProductionSourceKey = "telemetry.sources.pvProductionWatts";
    public const string TelemetryBatterySocSourceKey = "telemetry.sources.batterySocPercent";
    public const string ConsumptionForecastAverageDailyConsumptionKwhKey = "consumptionForecast.averageDailyConsumptionKwh";
    public const string ConsumptionForecastTimeZoneKey = "consumptionForecast.timeZone";
    public const string DecisionLogRetentionDaysKey = "decisionLog.retentionDays";
    public const string DecisionWorkerIntervalSecondsKey = "decisionWorker.intervalSeconds";
    public const string DecisionDirectionChangeHoldCyclesKey = "decision.directionChangeHoldCycles";
    public const string DecisionDirectionChangeMinimumPreviousPowerWattsKey = "decision.directionChangeMinimumPreviousPowerWatts";
    public const string DecisionDirectionChangeMaximumNewPowerWattsKey = "decision.directionChangeMaximumNewPowerWatts";
    public const string ManualChargePowerWattsKey = "manualCharge.powerWatts";
    public const string ManualChargeExpiresAtUtcKey = "manualCharge.expiresAtUtc";
    public const string WorkerFailureEmailEnabledKey = "notifications.workerFailureEmail.enabled";
    public const string WorkerFailureEmailSmtpHostKey = "notifications.workerFailureEmail.smtpHost";
    public const string WorkerFailureEmailSmtpPortKey = "notifications.workerFailureEmail.smtpPort";
    public const string WorkerFailureEmailSmtpUsernameKey = "notifications.workerFailureEmail.smtpUsername";
    public const string WorkerFailureEmailSmtpPasswordKey = "notifications.workerFailureEmail.smtpPassword";
    public const string WorkerFailureEmailFromAddressKey = "notifications.workerFailureEmail.fromAddress";
    public const string WorkerFailureEmailToAddressKey = "notifications.workerFailureEmail.toAddress";
    public const string WorkerFailureEmailEnableSslKey = "notifications.workerFailureEmail.enableSsl";
    public const string WorkerFailureEmailSubjectPrefixKey = "notifications.workerFailureEmail.subjectPrefix";
    public const string DashboardAutoRefreshIntervalSecondsKey = "dashboard.autoRefreshIntervalSeconds";
    public const string ForecastHorizonHoursKey = "forecast.horizonHours";
    public const string GridFeedInCompensationPricePerKwhKey = "gridFeedIn.compensationPricePerKwh";
    public const string PvForecastProviderKey = "pvForecast.provider";
    public const string PvForecastApiEndpointKey = "pvForecast.apiEndpoint";
    public const string PvForecastApiKeyKey = "pvForecast.apiKey";
    public const string PvForecastLatitudeKey = "pvForecast.latitude";
    public const string PvForecastLongitudeKey = "pvForecast.longitude";
    public const string PvForecastPeakPowerKwpKey = "pvForecast.peakPowerKwp";
    public const string PvForecastDeclinationDegreesKey = "pvForecast.declinationDegrees";
    public const string PvForecastAzimuthDegreesKey = "pvForecast.azimuthDegrees";
    public const string PvForecastTimeZoneKey = "pvForecast.timeZone";
    public const string TibberApiEndpointKey = "tibber.apiEndpoint";
    public const string TibberHomeSelectionKey = "tibber.homeSelection";
    public const string TibberAccessTokenKey = "tibber.accessToken";
    public const string MqttHostKey = "mqtt.host";
    public const string MqttPortKey = "mqtt.port";
    public const string MqttUsernameKey = "mqtt.username";
    public const string MqttPasswordKey = "mqtt.password";
    public const string VictronHostKey = "victron.host";
    public const string VictronPortKey = "victron.port";
    public const string VictronPortalIdKey = "victron.portalId";
    public const string VictronKeepAliveSecondsKey = "victron.keepAliveSeconds";
    public const string VictronStaleAfterSecondsKey = "victron.staleAfterSeconds";
    public const string VictronDryRunKey = "victron.dryRun";
    public const string VictronControlModeKey = "victron.controlMode";
    public const string VictronTopicGridPowerKey = "victron.topics.gridPower";
    public const string VictronTopicBatterySocKey = "victron.topics.batterySoc";
    public const string VictronTopicBatteryPowerKey = "victron.topics.batteryPower";
    public const string VictronTopicHouseConsumptionKey = "victron.topics.houseConsumption";
    public const string VictronWriteTopicChargeDischargeSetpointKey = "victron.writeTopics.chargeDischargeSetpoint";
    public const string VictronWriteTopicHub4ModeKey = "victron.writeTopics.hub4Mode";
    public const string VictronExternalEssPhaseCountKey = "victron.externalEss.phaseCount";
    public const string VictronExternalEssSwitchModeViaMqttKey = "victron.externalEss.switchModeViaMqtt";
    public const string VictronExternalEssL1AcPowerSetpointTopicKey = "victron.externalEss.writeTopics.l1AcPowerSetpoint";
    public const string VictronExternalEssL2AcPowerSetpointTopicKey = "victron.externalEss.writeTopics.l2AcPowerSetpoint";
    public const string VictronExternalEssL3AcPowerSetpointTopicKey = "victron.externalEss.writeTopics.l3AcPowerSetpoint";
    public const string VictronWriteTopicDisableChargeKey = "victron.writeTopics.disableCharge";
    public const string VictronWriteTopicDisableFeedInKey = "victron.writeTopics.disableFeedIn";
    public const string VictronBatteryIdleThresholdWattsKey = "victron.batteryIdleThresholdWatts";
    public const string HagerEnergyApiBaseUrlKey = "hagerEnergy.apiBaseUrl";
    public const string HagerEnergyAuthorizationEndpointKey = "hagerEnergy.authorizationEndpoint";
    public const string HagerEnergyTokenEndpointKey = "hagerEnergy.tokenEndpoint";
    public const string HagerEnergyRedirectUriKey = "hagerEnergy.redirectUri";
    public const string HagerEnergyPostLoginRedirectUrlKey = "hagerEnergy.postLoginRedirectUrl";
    public const string HagerEnergyScopeKey = "hagerEnergy.scope";
    public const string HagerEnergyOAuthStateKey = "hagerEnergy.oauthState";
    public const string HagerEnergyApiKeyKey = "hagerEnergy.apiKey";
    public const string HagerEnergyClientIdKey = "hagerEnergy.clientId";
    public const string HagerEnergyClientSecretKey = "hagerEnergy.clientSecret";
    public const string HagerEnergyRefreshTokenKey = "hagerEnergy.refreshToken";
    public const string HagerEnergyAccessTokenKey = "hagerEnergy.accessToken";
    public const string HagerEnergyInstallationIdKey = "hagerEnergy.installationId";
    public const string HagerEnergyGridImportJsonPathKey = "hagerEnergy.jsonPaths.gridImportWatts";
    public const string HagerEnergyPvProductionJsonPathKey = "hagerEnergy.jsonPaths.pvProductionWatts";
    public const string HagerEnergyBatterySocJsonPathKey = "hagerEnergy.jsonPaths.batterySocPercent";

    private static readonly ControllerSettingDefinition[] Definitions =
    {
        new(BatteryTotalCapacityKwhKey, "10", ControllerSettingSensitivity.Normal),
        new(BatteryMinimumStateOfChargePercentKey, "10", ControllerSettingSensitivity.Normal),
        new(BatteryMaximumChargePowerWattsKey, "3000", ControllerSettingSensitivity.Normal),
        new(BatteryMaximumDischargePowerWattsKey, "3000", ControllerSettingSensitivity.Normal),
        new(BatteryRoundTripEfficiencyPercentKey, "90", ControllerSettingSensitivity.Normal),
        new(BatteryTargetEndStateOfChargePercentKey, "25", ControllerSettingSensitivity.Normal),
        new(BatteryPlanningMinimumStateOfChargePercentKey, "15", ControllerSettingSensitivity.Normal),
        new(BatteryPlanningMaximumStateOfChargePercentKey, "95", ControllerSettingSensitivity.Normal),
        new(BatteryTemporaryStateOfChargePercentKey, "55", ControllerSettingSensitivity.Normal),
        new(TelemetryTemporaryGridImportWattsKey, "0", ControllerSettingSensitivity.Normal),
        new(TelemetryTemporaryPvProductionWattsKey, "0", ControllerSettingSensitivity.Normal),
        new(TelemetryGridPowerDeadbandWattsKey, "30", ControllerSettingSensitivity.Normal),
        new(TelemetryGridImportSourceKey, "victronMqtt", ControllerSettingSensitivity.Normal),
        new(TelemetryPvProductionSourceKey, "victronMqtt", ControllerSettingSensitivity.Normal),
        new(TelemetryBatterySocSourceKey, "victronMqtt", ControllerSettingSensitivity.Normal),
        new(ConsumptionForecastAverageDailyConsumptionKwhKey, "24", ControllerSettingSensitivity.Normal),
        new(ConsumptionForecastTimeZoneKey, "Europe/Berlin", ControllerSettingSensitivity.Normal),
        new(DecisionLogRetentionDaysKey, "90", ControllerSettingSensitivity.Normal),
        new(DecisionWorkerIntervalSecondsKey, "60", ControllerSettingSensitivity.Normal),
        new(DecisionDirectionChangeHoldCyclesKey, "2", ControllerSettingSensitivity.Normal),
        new(DecisionDirectionChangeMinimumPreviousPowerWattsKey, "1000", ControllerSettingSensitivity.Normal),
        new(DecisionDirectionChangeMaximumNewPowerWattsKey, "500", ControllerSettingSensitivity.Normal),
        new(ManualChargePowerWattsKey, "0", ControllerSettingSensitivity.Normal),
        new(ManualChargeExpiresAtUtcKey, "1970-01-01T00:00:00.0000000+00:00", ControllerSettingSensitivity.Normal),
        new(WorkerFailureEmailEnabledKey, "false", ControllerSettingSensitivity.Normal),
        new(WorkerFailureEmailSmtpHostKey, "localhost", ControllerSettingSensitivity.Normal),
        new(WorkerFailureEmailSmtpPortKey, "25", ControllerSettingSensitivity.Normal),
        new(WorkerFailureEmailSmtpUsernameKey, null, ControllerSettingSensitivity.Sensitive),
        new(WorkerFailureEmailSmtpPasswordKey, null, ControllerSettingSensitivity.Sensitive),
        new(WorkerFailureEmailFromAddressKey, "controller@localhost", ControllerSettingSensitivity.Normal),
        new(WorkerFailureEmailToAddressKey, "operator@localhost", ControllerSettingSensitivity.Normal),
        new(WorkerFailureEmailEnableSslKey, "false", ControllerSettingSensitivity.Normal),
        new(WorkerFailureEmailSubjectPrefixKey, "EnergyFlowPilot", ControllerSettingSensitivity.Normal),
        new(DashboardAutoRefreshIntervalSecondsKey, "60", ControllerSettingSensitivity.Normal),
        new(ForecastHorizonHoursKey, "24", ControllerSettingSensitivity.Normal),
        new(GridFeedInCompensationPricePerKwhKey, "0.08", ControllerSettingSensitivity.Normal),
        new(PvForecastProviderKey, "forecastSolarPublic", ControllerSettingSensitivity.Normal),
        new(PvForecastApiEndpointKey, "https://api.forecast.solar/estimate", ControllerSettingSensitivity.Normal),
        new(PvForecastApiKeyKey, null, ControllerSettingSensitivity.Sensitive),
        new(PvForecastLatitudeKey, "52.52", ControllerSettingSensitivity.Normal),
        new(PvForecastLongitudeKey, "13.405", ControllerSettingSensitivity.Normal),
        new(PvForecastPeakPowerKwpKey, "10", ControllerSettingSensitivity.Normal),
        new(PvForecastDeclinationDegreesKey, "35", ControllerSettingSensitivity.Normal),
        new(PvForecastAzimuthDegreesKey, "0", ControllerSettingSensitivity.Normal),
        new(PvForecastTimeZoneKey, "Europe/Berlin", ControllerSettingSensitivity.Normal),
        new(TibberApiEndpointKey, "https://api.tibber.com/v1-beta/gql", ControllerSettingSensitivity.Normal),
        new(TibberHomeSelectionKey, "first", ControllerSettingSensitivity.Normal),
        new(TibberAccessTokenKey, null, ControllerSettingSensitivity.Sensitive),
        new(MqttHostKey, "localhost", ControllerSettingSensitivity.Normal),
        new(MqttPortKey, "1883", ControllerSettingSensitivity.Normal),
        new(MqttUsernameKey, null, ControllerSettingSensitivity.Sensitive),
        new(MqttPasswordKey, null, ControllerSettingSensitivity.Sensitive),
        new(VictronHostKey, "192.168.69.92", ControllerSettingSensitivity.Normal),
        new(VictronPortKey, "1883", ControllerSettingSensitivity.Normal),
        new(VictronPortalIdKey, "portal-id", ControllerSettingSensitivity.Normal),
        new(VictronKeepAliveSecondsKey, "15", ControllerSettingSensitivity.Normal),
        new(VictronStaleAfterSecondsKey, "30", ControllerSettingSensitivity.Normal),
        new(VictronDryRunKey, "true", ControllerSettingSensitivity.Normal),
        new(VictronControlModeKey, "normalEss", ControllerSettingSensitivity.Normal),
        new(VictronTopicGridPowerKey, "N/{portalId}/grid/30/Ac/Power", ControllerSettingSensitivity.Normal),
        new(VictronTopicBatterySocKey, "N/{portalId}/battery/512/Soc", ControllerSettingSensitivity.Normal),
        new(VictronTopicBatteryPowerKey, "N/{portalId}/battery/512/Dc/0/Power", ControllerSettingSensitivity.Normal),
        new(VictronTopicHouseConsumptionKey, "N/{portalId}/system/0/Ac/Consumption/L1/Power", ControllerSettingSensitivity.Normal),
        new(VictronWriteTopicChargeDischargeSetpointKey, "W/{portalId}/settings/0/Settings/CGwacs/AcPowerSetPoint", ControllerSettingSensitivity.Normal),
        new(VictronWriteTopicHub4ModeKey, "W/{portalId}/settings/0/Settings/CGwacs/Hub4Mode", ControllerSettingSensitivity.Normal),
        new(VictronExternalEssPhaseCountKey, "1", ControllerSettingSensitivity.Normal),
        new(VictronExternalEssSwitchModeViaMqttKey, "false", ControllerSettingSensitivity.Normal),
        new(VictronExternalEssL1AcPowerSetpointTopicKey, "W/{portalId}/vebus/276/Hub4/L1/AcPowerSetpoint", ControllerSettingSensitivity.Normal),
        new(VictronExternalEssL2AcPowerSetpointTopicKey, "W/{portalId}/vebus/276/Hub4/L2/AcPowerSetpoint", ControllerSettingSensitivity.Normal),
        new(VictronExternalEssL3AcPowerSetpointTopicKey, "W/{portalId}/vebus/276/Hub4/L3/AcPowerSetpoint", ControllerSettingSensitivity.Normal),
        new(VictronWriteTopicDisableChargeKey, "W/{portalId}/vebus/276/Hub4/DisableCharge", ControllerSettingSensitivity.Normal),
        new(VictronWriteTopicDisableFeedInKey, "W/{portalId}/vebus/276/Hub4/DisableFeedIn", ControllerSettingSensitivity.Normal),
        new(VictronBatteryIdleThresholdWattsKey, "100", ControllerSettingSensitivity.Normal),
        new(HagerEnergyApiBaseUrlKey, "https://api.hagerenergy.com", ControllerSettingSensitivity.Normal),
        new(HagerEnergyAuthorizationEndpointKey, "https://auth.hagerenergy.com/realms/customer/.well-known/uma2-configuration", ControllerSettingSensitivity.Normal),
        new(HagerEnergyTokenEndpointKey, "https://auth.hagerenergy.com/realms/customer/protocol/openid-connect/token", ControllerSettingSensitivity.Normal),
        new(HagerEnergyRedirectUriKey, "http://localhost:5094/api/hager-energy/oauth/callback", ControllerSettingSensitivity.Normal),
        new(HagerEnergyPostLoginRedirectUrlKey, "http://localhost:5173/settings?section=system", ControllerSettingSensitivity.Normal),
        new(HagerEnergyScopeKey, "read:storage", ControllerSettingSensitivity.Normal),
        new(HagerEnergyOAuthStateKey, null, ControllerSettingSensitivity.Sensitive),
        new(HagerEnergyApiKeyKey, null, ControllerSettingSensitivity.Sensitive),
        new(HagerEnergyClientIdKey, null, ControllerSettingSensitivity.Sensitive),
        new(HagerEnergyClientSecretKey, null, ControllerSettingSensitivity.Sensitive),
        new(HagerEnergyRefreshTokenKey, null, ControllerSettingSensitivity.Sensitive),
        new(HagerEnergyAccessTokenKey, null, ControllerSettingSensitivity.Sensitive),
        new(HagerEnergyInstallationIdKey, null, ControllerSettingSensitivity.Sensitive),
        new(HagerEnergyGridImportJsonPathKey, "data.gridPower", ControllerSettingSensitivity.Normal),
        new(HagerEnergyPvProductionJsonPathKey, "data.pvProduction", ControllerSettingSensitivity.Normal),
        new(HagerEnergyBatterySocJsonPathKey, "data.batteryStateOfCharge", ControllerSettingSensitivity.Normal)
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
