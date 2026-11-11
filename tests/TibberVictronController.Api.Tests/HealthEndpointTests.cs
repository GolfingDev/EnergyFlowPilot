using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.Health;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
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
        workerRuntimeStatus.MarkFailed("Verbindung zum Strompreisabruf fehlgeschlagen.", checkedAtUtc);

        var result = HealthEndpoints.GetHealthAsync(
            new FakeUtcClock(checkedAtUtc),
            mqttRuntimeStatus,
            workerRuntimeStatus);

        var jsonResult = Assert.IsType<JsonHttpResult<HealthResponseDto>>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, jsonResult.StatusCode);
        Assert.Equal("Unhealthy", jsonResult.Value!.Status);
    }

    [Fact]
    public void AnonymousHealthNeverContainsExceptionMessages()
    {
        var mqttRuntimeStatus = new VictronMqttRuntimeStatus();
        var workerRuntimeStatus = new DecisionWorkerRuntimeStatus();
        var checkedAtUtc = new DateTimeOffset(2026, 5, 3, 8, 0, 0, TimeSpan.Zero);
        mqttRuntimeStatus.MarkConnected();
        workerRuntimeStatus.MarkFailed("Connection refused (192.0.2.15:1883)", checkedAtUtc);

        var result = HealthEndpoints.GetHealthAsync(new FakeUtcClock(checkedAtUtc), mqttRuntimeStatus, workerRuntimeStatus);

        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<JsonHttpResult<HealthResponseDto>>(result).Value);
        Assert.DoesNotContain("192.0.2.15", json);
        Assert.Contains("Decision-Worker ist fehlgeschlagen.", json);
    }

    [Theory]
    [InlineData("Disabled", false, 0, "Healthy", 200)]
    [InlineData("Active", true, 1, "Healthy", 200)]
    [InlineData("Blocked", false, 600, "Degraded", 200)]
    [InlineData("Restoring", true, 2, "Degraded", 200)]
    [InlineData("Active", true, 31, "Unhealthy", 503)]
    public void JointExecutionIsPartOfTheHealth(string status, bool ownsControl, int ageSeconds, string expected, int httpStatus)
    {
        var checkedAtUtc = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var mqttRuntimeStatus = new VictronMqttRuntimeStatus();
        var workerRuntimeStatus = new DecisionWorkerRuntimeStatus();
        mqttRuntimeStatus.MarkConnected();
        workerRuntimeStatus.MarkSuccessful(checkedAtUtc);
        var joint = new JointBatteryExecutionState();
        joint.Set(new JointBatteryExecutionSnapshot(status, "Test", checkedAtUtc.AddSeconds(-ageSeconds), ownsControl));

        var result = HealthEndpoints.GetHealthAsync(new FakeUtcClock(checkedAtUtc), mqttRuntimeStatus, workerRuntimeStatus, joint);

        var jsonResult = Assert.IsType<JsonHttpResult<HealthResponseDto>>(result);
        Assert.Equal(expected, jsonResult.Value!.JointExecution.Status);
        Assert.Equal(expected, jsonResult.Value.Status);
        Assert.Equal(httpStatus, jsonResult.StatusCode);
    }

    [Fact]
    public async Task UnexpectedApiErrorReturnsOnlyAFixedMessageAndTheTraceId()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "efp-health-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var middleware = new TibberVictronController.Api.Diagnostics.FileExceptionLoggingMiddleware(
                _ => throw new InvalidOperationException("Verbindung zu http://192.0.2.15/api?token=secret fehlgeschlagen"),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TibberVictronController.Api.Diagnostics.FileExceptionLoggingMiddleware>.Instance,
                Microsoft.Extensions.Options.Options.Create(new TibberVictronController.Api.Diagnostics.FileExceptionLogOptions { LogDirectory = logDirectory }));
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
            Assert.Contains("traceId", body);
            Assert.DoesNotContain("192.0.2.15", body);
            Assert.DoesNotContain("InvalidOperationException", body);
        }
        finally
        {
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
        }
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
