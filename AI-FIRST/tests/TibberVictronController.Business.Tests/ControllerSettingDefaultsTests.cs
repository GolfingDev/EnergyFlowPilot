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
    public void CreateDefaultSettingsContainsGridFeedInCompensationDefault()
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc);

        var gridFeedInCompensationSetting = settings.Single(setting =>
            setting.Key == ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey);

        Assert.Equal("0.08", gridFeedInCompensationSetting.Value);
        Assert.Equal(ControllerSettingSensitivity.Normal, gridFeedInCompensationSetting.Sensitivity);
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
}
