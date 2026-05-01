using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class ControllerSettingTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConstructorRejectsMissingKey()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ControllerSetting(" ", "value", ControllerSettingSensitivity.Normal, UpdatedAtUtc));

        Assert.Contains("Der Einstellungsschluessel muss angegeben werden.", exception.Message);
    }

    [Fact]
    public void ConstructorRejectsNonUtcUpdateTime()
    {
        var updatedAtLocalOffset = new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var exception = Assert.Throws<ArgumentException>(
            () => new ControllerSetting("battery.totalCapacityKwh", "10", ControllerSettingSensitivity.Normal, updatedAtLocalOffset));

        Assert.Contains("Der Aktualisierungszeitpunkt der Einstellung muss in UTC angegeben sein.", exception.Message);
    }

    [Fact]
    public void GetFrontendReadableValueReturnsNormalSettingValue()
    {
        var setting = new ControllerSetting("battery.totalCapacityKwh", "10", ControllerSettingSensitivity.Normal, UpdatedAtUtc);

        Assert.True(setting.IsConfigured);
        Assert.Equal("10", setting.GetFrontendReadableValue());
    }

    [Fact]
    public void GetFrontendReadableValueHidesSensitiveSettingValue()
    {
        var setting = new ControllerSetting("tibber.accessToken", "secret-token", ControllerSettingSensitivity.Sensitive, UpdatedAtUtc);

        Assert.True(setting.IsConfigured);
        Assert.Null(setting.GetFrontendReadableValue());
    }

    [Fact]
    public void IsConfiguredReturnsFalseWhenValueIsMissing()
    {
        var setting = new ControllerSetting("mqtt.password", null, ControllerSettingSensitivity.Sensitive, UpdatedAtUtc);

        Assert.False(setting.IsConfigured);
        Assert.Null(setting.GetFrontendReadableValue());
    }
}
