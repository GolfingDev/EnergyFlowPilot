using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class ControllerSettingDefaultsTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetDefinitionsContainsUniqueKeys()
    {
        var definitions = ControllerSettingDefaults.GetDefinitions();
        var distinctKeyCount = definitions
            .Select(definition => definition.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.Equal(definitions.Count, distinctKeyCount);
    }

    [Fact]
    public void CreateDefaultSettingsContainsBatteryCapacityDefault()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        var batteryCapacitySetting = settings.Single(setting =>
            setting.Key == ControllerSettingDefaults.BatteryTotalCapacityKwhKey);

        Assert.Equal("10", batteryCapacitySetting.Value);
        Assert.Equal(ControllerSettingSensitivity.Normal, batteryCapacitySetting.Sensitivity);
        Assert.Equal(UpdatedAtUtc, batteryCapacitySetting.UpdatedAtUtc);
    }

    [Fact]
    public void CreateDefaultSettingsContainsDecisionLogRetentionDefault()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        var decisionLogRetentionSetting = settings.Single(setting =>
            setting.Key == ControllerSettingDefaults.DecisionLogRetentionDaysKey);

        Assert.Equal("90", decisionLogRetentionSetting.Value);
        Assert.Equal(ControllerSettingSensitivity.Normal, decisionLogRetentionSetting.Sensitivity);
    }

    [Fact]
    public void CreateDefaultSettingsContainsDashboardAutoRefreshDefault()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        var dashboardRefreshSetting = settings.Single(setting =>
            setting.Key == ControllerSettingDefaults.DashboardAutoRefreshIntervalSecondsKey);

        Assert.Equal("60", dashboardRefreshSetting.Value);
        Assert.Equal(ControllerSettingSensitivity.Normal, dashboardRefreshSetting.Sensitivity);
    }

    [Fact]
    public void CreateDefaultSettingsContainsManualChargeDefaults()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        Assert.Equal("0", GetSettingValue(settings, ControllerSettingDefaults.ManualChargePowerWattsKey));
        Assert.Equal("1970-01-01T00:00:00.0000000+00:00", GetSettingValue(settings, ControllerSettingDefaults.ManualChargeExpiresAtUtcKey));
    }

    [Fact]
    public void CreateDefaultSettingsContainsGridFeedInCompensationDefault()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        var gridFeedInCompensationSetting = settings.Single(setting =>
            setting.Key == ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey);

        Assert.Equal("0.08", gridFeedInCompensationSetting.Value);
        Assert.Equal(ControllerSettingSensitivity.Normal, gridFeedInCompensationSetting.Sensitivity);
    }

    [Fact]
    public void CreateDefaultSettingsContainsForecastSolarPublicDefaults()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        Assert.Equal("forecastSolarPublic", GetSettingValue(settings, ControllerSettingDefaults.PvForecastProviderKey));
        Assert.Equal("https://api.forecast.solar/estimate", GetSettingValue(settings, ControllerSettingDefaults.PvForecastApiEndpointKey));
        Assert.Null(GetSettingValue(settings, ControllerSettingDefaults.PvForecastApiKeyKey));
        Assert.Equal("52.52", GetSettingValue(settings, ControllerSettingDefaults.PvForecastLatitudeKey));
        Assert.Equal("13.405", GetSettingValue(settings, ControllerSettingDefaults.PvForecastLongitudeKey));
        Assert.Equal("10", GetSettingValue(settings, ControllerSettingDefaults.PvForecastPeakPowerKwpKey));
        Assert.Equal("35", GetSettingValue(settings, ControllerSettingDefaults.PvForecastDeclinationDegreesKey));
        Assert.Equal("0", GetSettingValue(settings, ControllerSettingDefaults.PvForecastAzimuthDegreesKey));
        Assert.Equal("Europe/Berlin", GetSettingValue(settings, ControllerSettingDefaults.PvForecastTimeZoneKey));
    }

    [Fact]
    public void CreateDefaultSettingsContainsTemporaryForecastInputDefaults()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        Assert.Equal("55", GetSettingValue(settings, ControllerSettingDefaults.BatteryTemporaryStateOfChargePercentKey));
        Assert.Equal("25", GetSettingValue(settings, ControllerSettingDefaults.BatteryTargetEndStateOfChargePercentKey));
        Assert.Equal("15", GetSettingValue(settings, ControllerSettingDefaults.BatteryPlanningMinimumStateOfChargePercentKey));
        Assert.Equal("95", GetSettingValue(settings, ControllerSettingDefaults.BatteryPlanningMaximumStateOfChargePercentKey));
        Assert.Equal("24", GetSettingValue(settings, ControllerSettingDefaults.ConsumptionForecastAverageDailyConsumptionKwhKey));
        Assert.Equal("Europe/Berlin", GetSettingValue(settings, ControllerSettingDefaults.ConsumptionForecastTimeZoneKey));
    }

    [Fact]
    public void CreateDefaultSettingsContainsGridPowerDeadbandDefault()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        Assert.Equal("30", GetSettingValue(settings, ControllerSettingDefaults.TelemetryGridPowerDeadbandWattsKey));
    }

    [Fact]
    public void CreateDefaultSettingsContainsVictronControlModeDefaults()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        Assert.Equal("normalEss", GetSettingValue(settings, ControllerSettingDefaults.VictronControlModeKey));
        Assert.Equal("false", GetSettingValue(settings, ControllerSettingDefaults.VictronExternalEssSwitchModeViaMqttKey));
        Assert.Equal("W/{portalId}/settings/0/Settings/CGwacs/Hub4Mode", GetSettingValue(settings, ControllerSettingDefaults.VictronWriteTopicHub4ModeKey));
        Assert.Equal("1", GetSettingValue(settings, ControllerSettingDefaults.VictronExternalEssPhaseCountKey));
        Assert.Equal("W/{portalId}/vebus/276/Hub4/L1/AcPowerSetpoint", GetSettingValue(settings, ControllerSettingDefaults.VictronExternalEssL1AcPowerSetpointTopicKey));
        Assert.Equal("W/{portalId}/vebus/276/Hub4/L2/AcPowerSetpoint", GetSettingValue(settings, ControllerSettingDefaults.VictronExternalEssL2AcPowerSetpointTopicKey));
        Assert.Equal("W/{portalId}/vebus/276/Hub4/L3/AcPowerSetpoint", GetSettingValue(settings, ControllerSettingDefaults.VictronExternalEssL3AcPowerSetpointTopicKey));
    }

    [Fact]
    public void CreateDefaultSettingsContainsHagerEnergyApiDefaults()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        Assert.Equal("victronMqtt", GetSettingValue(settings, ControllerSettingDefaults.TelemetryGridImportSourceKey));
        Assert.Equal("victronMqtt", GetSettingValue(settings, ControllerSettingDefaults.TelemetryPvProductionSourceKey));
        Assert.Equal("victronMqtt", GetSettingValue(settings, ControllerSettingDefaults.TelemetryBatterySocSourceKey));
        Assert.Equal("https://api.hagerenergy.com", GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyApiBaseUrlKey));
        Assert.Equal("https://auth.hagerenergy.com/realms/customer/.well-known/uma2-configuration", GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyAuthorizationEndpointKey));
        Assert.Equal("read:storage", GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyScopeKey));
        Assert.Equal("data.gridPower", GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyGridImportJsonPathKey));
        Assert.Equal("data.pvProduction", GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyPvProductionJsonPathKey));
        Assert.Equal("data.batteryStateOfCharge", GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyBatterySocJsonPathKey));
        Assert.Null(GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyApiKeyKey));
        Assert.Null(GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyRefreshTokenKey));
        Assert.Null(GetSettingValue(settings, ControllerSettingDefaults.HagerEnergyInstallationIdKey));
    }

    [Fact]
    public void CreateDefaultSettingsKeepsSensitiveAccessDataUnconfigured()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        var tibberAccessTokenSetting = settings.Single(setting =>
            setting.Key == ControllerSettingDefaults.TibberAccessTokenKey);

        Assert.False(tibberAccessTokenSetting.IsConfigured);
        Assert.Equal(ControllerSettingSensitivity.Sensitive, tibberAccessTokenSetting.Sensitivity);
        Assert.Null(tibberAccessTokenSetting.GetFrontendReadableValue());
    }

    [Fact]
    public void CreateDefaultSettingsRejectsNonUtcSeedTime()
    {
        var updatedAtLocalOffset = new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var exception = Assert.Throws<ArgumentException>(
            () => ControllerSettingDefaults.CreateDefaultSettings(updatedAtLocalOffset));

        Assert.Contains("Der Aktualisierungszeitpunkt fuer Default-Einstellungen muss in UTC angegeben sein.", exception.Message);
    }

    [Fact]
    public void ControllerSettingDefinitionRejectsNormalSettingWithoutDefault()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ControllerSettingDefinition("setting.withoutDefault", null, ControllerSettingSensitivity.Normal));

        Assert.Contains("Eine normale Einstellung braucht einen Default-Wert.", exception.Message);
    }

    private static string? GetSettingValue(IReadOnlyList<ControllerSetting> settings, string key)
    {
        return settings.Single(setting => setting.Key == key).Value;
    }
}
