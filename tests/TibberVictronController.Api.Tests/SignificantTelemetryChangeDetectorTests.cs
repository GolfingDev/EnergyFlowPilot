using TibberVictronController.Api.Configuration;

namespace TibberVictronController.Api.Tests;

public sealed class SignificantTelemetryChangeDetectorTests
{
    [Theory]
    [InlineData(1000, 1090, false)]
    [InlineData(1000, 1100, true)]
    [InlineData(1000, 1300, true)]
    [InlineData(20, 80, false)]
    [InlineData(20, 320, true)]
    public void IsSignificantPowerChangeUsesAbsoluteAndRelativeThresholds(
        decimal previousValue,
        decimal currentValue,
        bool expectedResult)
    {
        Assert.Equal(
            expectedResult,
            SignificantTelemetryChangeDetector.IsSignificantPowerChange(previousValue, currentValue));
    }

    [Theory]
    [InlineData(92.0, 92.4, false)]
    [InlineData(92.0, 92.5, true)]
    [InlineData(92.0, 91.5, true)]
    public void IsSignificantStateOfChargeChangeUsesHalfPercentThreshold(
        decimal previousValue,
        decimal currentValue,
        bool expectedResult)
    {
        Assert.Equal(
            expectedResult,
            SignificantTelemetryChangeDetector.IsSignificantStateOfChargeChange(previousValue, currentValue));
    }

    [Fact]
    public void ShouldTriggerUsesFirstValueAsBaseline()
    {
        var detector = new SignificantTelemetryChangeDetector();

        var firstResult = detector.ShouldTrigger("topic", 1000m, TelemetrySignalKind.Power);
        var secondResult = detector.ShouldTrigger("topic", 1300m, TelemetrySignalKind.Power);

        Assert.False(firstResult);
        Assert.True(secondResult);
    }
}
