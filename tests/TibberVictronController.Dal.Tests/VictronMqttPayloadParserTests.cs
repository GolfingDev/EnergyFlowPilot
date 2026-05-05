using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Dal.Tests;

public sealed class VictronMqttPayloadParserTests
{
    [Theory]
    [InlineData("42.5", 42.5)]
    [InlineData("{\"value\":-1200}", -1200)]
    public void TryParseDecimalReadsDirectAndJsonPayloads(string payload, decimal expectedValue)
    {
        var result = VictronMqttPayloadParser.TryParseDecimal(payload, out var value);

        Assert.True(result);
        Assert.Equal(expectedValue, value);
    }
}
