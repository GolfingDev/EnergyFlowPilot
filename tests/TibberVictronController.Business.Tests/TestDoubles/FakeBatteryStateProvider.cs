using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests.TestDoubles;

internal sealed class FakeBatteryStateProvider : IBatteryStateProvider
{
    private readonly BatteryState currentBatteryState;

    public FakeBatteryStateProvider()
        : this(new BatteryState(55m, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)))
    {
    }

    public FakeBatteryStateProvider(BatteryState currentBatteryState)
    {
        this.currentBatteryState = currentBatteryState;
    }

    public Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(currentBatteryState);
    }
}
