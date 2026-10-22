using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Weishaupt;

namespace TibberVictronController.Dal.Tests;

public sealed class WeishauptPollingIntervalTests
{
    [Theory]
    [InlineData("7200", 3600)]
    [InlineData("3600", 3600)]
    [InlineData("300", 300)]
    [InlineData("1", 60)]
    [InlineData("invalid", 300)]
    public async Task LegacyIntervalsAreBoundedToAtLeastHourlyCollection(string configured, int expected)
    {
        var now = DateTimeOffset.UtcNow;
        var store = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(now));
        await store.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.WeishauptBiblockModbusPollingIntervalSecondsKey,
            configured, ControllerSettingSensitivity.Normal, now));
        var settings = await new DatabaseWeishauptBiblockModbusSettingsProvider(store).GetSettingsAsync();
        Assert.Equal(expected, settings.PollingIntervalSeconds);
    }
}
