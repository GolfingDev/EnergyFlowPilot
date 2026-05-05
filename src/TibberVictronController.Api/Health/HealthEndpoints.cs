using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Health;

/// <summary>
/// Maps compact health endpoints for runtime monitoring and simple production checks.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", GetHealthAsync)
            .WithName("GetHealth")
            .WithTags("Health");

        return endpoints;
    }

    public static IResult GetHealthAsync(
        IUtcClock utcClock,
        VictronMqttRuntimeStatus mqttRuntimeStatus,
        DecisionWorkerRuntimeStatus workerRuntimeStatus)
    {
        var mqtt = BuildMqttComponent(mqttRuntimeStatus);
        var worker = BuildWorkerComponent(workerRuntimeStatus);
        var overallStatus = BuildOverallStatus(mqtt.Status, worker.Status);
        var response = new HealthResponseDto(
            overallStatus,
            utcClock.UtcNow,
            mqtt,
            worker);

        var httpStatusCode = string.Equals(overallStatus, "Unhealthy", StringComparison.Ordinal)
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        return TypedResults.Json(response, statusCode: httpStatusCode);
    }

    private static HealthComponentDto BuildMqttComponent(VictronMqttRuntimeStatus runtimeStatus)
    {
        return runtimeStatus.ConnectionState switch
        {
            "Connected" => new HealthComponentDto(
                "Healthy",
                "MQTT-Verbindung aktiv.",
                runtimeStatus.LastSuccessfulMessageAtUtc,
                runtimeStatus.LastErrorMessage),
            "Failed" => new HealthComponentDto(
                "Unhealthy",
                runtimeStatus.LastErrorMessage ?? "MQTT-Verbindung ist fehlgeschlagen.",
                runtimeStatus.LastSuccessfulMessageAtUtc,
                runtimeStatus.LastErrorMessage),
            "Starting" => new HealthComponentDto(
                "Starting",
                "MQTT-Verbindung wird aufgebaut.",
                runtimeStatus.LastSuccessfulMessageAtUtc,
                runtimeStatus.LastErrorMessage),
            "Stopped" => new HealthComponentDto(
                "Degraded",
                "MQTT-Hintergrunddienst ist gestoppt.",
                runtimeStatus.LastSuccessfulMessageAtUtc,
                runtimeStatus.LastErrorMessage),
            _ => new HealthComponentDto(
                "Starting",
                "MQTT-Hintergrunddienst wurde noch nicht gestartet.",
                runtimeStatus.LastSuccessfulMessageAtUtc,
                runtimeStatus.LastErrorMessage)
        };
    }

    private static HealthComponentDto BuildWorkerComponent(DecisionWorkerRuntimeStatus runtimeStatus)
    {
        return runtimeStatus.State switch
        {
            "Healthy" => new HealthComponentDto(
                "Healthy",
                "Decision-Worker läuft erfolgreich.",
                runtimeStatus.LastSuccessfulRunAtUtc,
                runtimeStatus.LastErrorMessage),
            "Failed" => new HealthComponentDto(
                "Unhealthy",
                runtimeStatus.LastErrorMessage ?? "Decision-Worker ist fehlgeschlagen.",
                runtimeStatus.LastFailureAtUtc,
                runtimeStatus.LastErrorMessage),
            "Starting" => new HealthComponentDto(
                "Starting",
                "Decision-Worker startet.",
                runtimeStatus.LastSuccessfulRunAtUtc,
                runtimeStatus.LastErrorMessage),
            "Stopped" => new HealthComponentDto(
                "Degraded",
                "Decision-Worker ist gestoppt.",
                runtimeStatus.LastSuccessfulRunAtUtc,
                runtimeStatus.LastErrorMessage),
            _ => new HealthComponentDto(
                "Starting",
                "Decision-Worker wurde noch nicht gestartet.",
                runtimeStatus.LastSuccessfulRunAtUtc,
                runtimeStatus.LastErrorMessage)
        };
    }

    private static string BuildOverallStatus(string mqttStatus, string workerStatus)
    {
        if (string.Equals(mqttStatus, "Unhealthy", StringComparison.Ordinal) ||
            string.Equals(workerStatus, "Unhealthy", StringComparison.Ordinal))
        {
            return "Unhealthy";
        }

        if (string.Equals(mqttStatus, "Degraded", StringComparison.Ordinal) ||
            string.Equals(workerStatus, "Degraded", StringComparison.Ordinal) ||
            string.Equals(mqttStatus, "Starting", StringComparison.Ordinal) ||
            string.Equals(workerStatus, "Starting", StringComparison.Ordinal))
        {
            return "Degraded";
        }

        return "Healthy";
    }
}

public sealed record HealthResponseDto(
    string Status,
    DateTimeOffset CheckedAtUtc,
    HealthComponentDto Mqtt,
    HealthComponentDto Worker);

public sealed record HealthComponentDto(
    string Status,
    string Message,
    DateTimeOffset? LastUpdatedAtUtc,
    string? LastErrorMessage);
