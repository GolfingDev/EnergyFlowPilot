using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests.TestDoubles;

internal sealed class FakeBatteryConfigurationProvider : IBatteryConfigurationProvider
{
    private readonly BatteryConfiguration batteryConfiguration;

    public FakeBatteryConfigurationProvider()
        : this(new BatteryConfiguration(10m))
    {
    }

    public FakeBatteryConfigurationProvider(BatteryConfiguration batteryConfiguration)
    {
        this.batteryConfiguration = batteryConfiguration;
    }

    public Task<BatteryConfiguration> GetBatteryConfigurationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(batteryConfiguration);
    }
}
