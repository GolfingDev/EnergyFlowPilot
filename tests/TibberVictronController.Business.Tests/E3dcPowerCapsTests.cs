using TibberVictronController.Business.Decisions;

namespace TibberVictronController.Business.Tests;

public sealed class E3dcPowerCapsTests
{
    [Theory]
    [InlineData(null, 0, null)]
    [InlineData(null, -5, null)]
    [InlineData(null, 1500, 1500)]
    [InlineData(3000, 0, 3000)]
    [InlineData(3000, 1500, 1500)]
    [InlineData(97, 1500, 97)]
    public void CombineKeepsTheStricterLimitAndTreatsZeroAsNone(int? reported, int configured, int? expected)
    {
        Assert.Equal(expected, E3dcPowerCaps.Combine(reported, configured));
    }
}
