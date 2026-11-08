using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Octopus;
using TibberVictronController.Dal.Tests.TestDoubles;

namespace TibberVictronController.Dal.Tests;

/// <summary>Missing settings name their keys, so the page that shows the message can offer to enter them.</summary>
public sealed class MissingSettingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingBatterySettingNamesItsKey()
    {
        var settings = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(Now)
            .Where(setting => setting.Key != ControllerSettingDefaults.BatteryTotalCapacityKwhKey));

        var error = await Assert.ThrowsAsync<MissingSettingException>(() => new DatabaseBatteryConfigurationProvider(settings).GetBatteryConfigurationAsync());

        Assert.Equal([ControllerSettingDefaults.BatteryTotalCapacityKwhKey], error.SettingKeys);
        Assert.IsAssignableFrom<InvalidOperationException>(error); // existing error handling keeps working
    }

    [Fact]
    public async Task OctopusWithoutSignInNamesEmailPasswordAndCustomerNumber()
    {
        var settings = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(Now));
        var provider = new OctopusPriceForecastProvider(new HttpClient(new RecordingHttpMessageHandler("{}")), settings, new OctopusPriceCache(),
            new FixedClock());

        var error = await Assert.ThrowsAsync<MissingSettingException>(() => provider.GetPriceForecastAsync(Now, Now.AddHours(1)));

        Assert.Equal([ControllerSettingDefaults.OctopusEmailKey, ControllerSettingDefaults.OctopusPasswordKey, ControllerSettingDefaults.OctopusAccountNumberKey],
            error.SettingKeys);
    }

    [Fact]
    public void KeysAreFoundInInnerExceptions()
    {
        var wrapped = new InvalidOperationException("Prognose fehlgeschlagen.", new MissingSettingException("fehlt", "a.b"));

        Assert.Equal(["a.b"], MissingSettingException.KeysOf(wrapped));
        Assert.Empty(MissingSettingException.KeysOf(new InvalidOperationException("anderer Fehler")));
    }

    private sealed class FixedClock : Business.Abstractions.IUtcClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
