using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.Health;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public void GetHealthAsyncReturnsHealthyWhenMqttAndWorkerAreHealthy()
    {
        var mqttRuntimeStatus = new VictronMqttRuntimeStatus();
        var workerRuntimeStatus = new DecisionWorkerRuntimeStatus();
        var checkedAtUtc = new DateTimeOffset(2026, 5, 3, 8, 0, 0, TimeSpan.Zero);

        mqttRuntimeStatus.MarkConnected();
        mqttRuntimeStatus.MarkMessageReceived(checkedAtUtc);
        workerRuntimeStatus.MarkSuccessful(checkedAtUtc);

        var result = HealthEndpoints.GetHealthAsync(
            new FakeUtcClock(checkedAtUtc),
            mqttRuntimeStatus,
            workerRuntimeStatus);

        var jsonResult = Assert.IsType<JsonHttpResult<HealthResponseDto>>(result);
        Assert.Equal(StatusCodes.Status200OK, jsonResult.StatusCode);
        Assert.Equal("Healthy", jsonResult.Value!.Status);
    }

    [Fact]
    public void GetHealthAsyncReturnsServiceUnavailableWhenWorkerFailed()
    {
        var mqttRuntimeStatus = new VictronMqttRuntimeStatus();
        var workerRuntimeStatus = new DecisionWorkerRuntimeStatus();
        var checkedAtUtc = new DateTimeOffset(2026, 5, 3, 8, 0, 0, TimeSpan.Zero);

        mqttRuntimeStatus.MarkConnected();
        workerRuntimeStatus.MarkFailed("Verbindung zum Tibber-Preisabruf fehlgeschlagen.", checkedAtUtc);

        var result = HealthEndpoints.GetHealthAsync(
            new FakeUtcClock(checkedAtUtc),
            mqttRuntimeStatus,
            workerRuntimeStatus);

        var jsonResult = Assert.IsType<JsonHttpResult<HealthResponseDto>>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, jsonResult.StatusCode);
        Assert.Equal("Unhealthy", jsonResult.Value!.Status);
    }

    private sealed class FakeUtcClock : IUtcClock
    {
        public FakeUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
