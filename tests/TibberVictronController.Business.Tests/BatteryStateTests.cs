using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryStateTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ConstructorRejectsInvalidStateOfCharge(decimal invalidStateOfChargePercent)
    {
        var measuredAtUtc = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryState(invalidStateOfChargePercent, measuredAtUtc));

        Assert.Contains("Der Akkuladestand muss zwischen 0 und 100 Prozent liegen.", exception.Message);
    }

    [Fact]
    public void ConstructorRejectsNonUtcMeasurementTime()
    {
        var measuredAtLocalOffset = new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var exception = Assert.Throws<ArgumentException>(
            () => new BatteryState(55m, measuredAtLocalOffset));

        Assert.Contains("Der Messzeitpunkt des Akkuladestands muss in UTC angegeben sein.", exception.Message);
    }
}
