using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Health;

/// <summary>
/// Maps compact health endpoints for runtime monitoring and simple production checks. The endpoint is anonymous, so it
/// reports fixed texts and timestamps only, never exception messages (they can carry addresses or URLs); details are
/// in the server log and, for signed-in users, in <c>/api/status</c>.
/// </summary>
public static class HealthEndpoints
{
    // While the joint execution owns the batteries it reports every second; a much older state means it is stuck.
    private static readonly TimeSpan JointExecutionStaleAfter = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Anonymous on purpose: the deployment script checks it locally after every update, before anyone signs in.
        endpoints.MapGet("/health", GetHealthAsync)
            .AllowAnonymous()
            .WithName("GetHealth")
            .WithTags("Health");

        return endpoints;
    }

    public static IResult GetHealthAsync(
        IUtcClock utcClock,
        VictronMqttRuntimeStatus mqttRuntimeStatus,
        DecisionWorkerRuntimeStatus workerRuntimeStatus,
        JointBatteryExecutionState? jointExecutionState = null)
    {
        var mqtt = BuildMqttComponent(mqttRuntimeStatus);
        var worker = BuildWorkerComponent(workerRuntimeStatus);
        var joint = BuildJointExecutionComponent(jointExecutionState?.Current, utcClock.UtcNow);
        var overallStatus = BuildOverallStatus(mqtt.Status, worker.Status, joint.Status);
        var response = new HealthResponseDto(
            overallStatus,
            utcClock.UtcNow,
            mqtt,
            worker,
            joint);

        var httpStatusCode = string.Equals(overallStatus, "Unhealthy", StringComparison.Ordinal)
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        return TypedResults.Json(response, statusCode: httpStatusCode);
    }

    private static HealthComponentDto BuildMqttComponent(VictronMqttRuntimeStatus runtimeStatus)
    {
        var lastMessage = runtimeStatus.LastSuccessfulMessageAtUtc;
        return runtimeStatus.ConnectionState switch
        {
            "Connected" => new HealthComponentDto("Healthy", "MQTT-Verbindung aktiv.", lastMessage),
            "Failed" => new HealthComponentDto("Unhealthy", "MQTT-Verbindung ist fehlgeschlagen.", lastMessage),
            "Starting" => new HealthComponentDto("Starting", "MQTT-Verbindung wird aufgebaut.", lastMessage),
            "Stopped" => new HealthComponentDto("Degraded", "MQTT-Hintergrunddienst ist gestoppt.", lastMessage),
            _ => new HealthComponentDto("Starting", "MQTT-Hintergrunddienst wurde noch nicht gestartet.", lastMessage)
        };
    }

    private static HealthComponentDto BuildWorkerComponent(DecisionWorkerRuntimeStatus runtimeStatus)
    {
        return runtimeStatus.State switch
        {
            "Healthy" => new HealthComponentDto("Healthy", "Decision-Worker läuft erfolgreich.", runtimeStatus.LastSuccessfulRunAtUtc),
            "Failed" => new HealthComponentDto("Unhealthy", "Decision-Worker ist fehlgeschlagen.", runtimeStatus.LastFailureAtUtc),
            "Starting" => new HealthComponentDto("Starting", "Decision-Worker startet.", runtimeStatus.LastSuccessfulRunAtUtc),
            "Stopped" => new HealthComponentDto("Degraded", "Decision-Worker ist gestoppt.", runtimeStatus.LastSuccessfulRunAtUtc),
            _ => new HealthComponentDto("Starting", "Decision-Worker wurde noch nicht gestartet.", runtimeStatus.LastSuccessfulRunAtUtc)
        };
    }

    /// <summary>
    /// While joint control is active, the legacy decision worker only projects the joint state, so its health says
    /// nothing about whether the batteries are actually controlled. A blocked or restoring execution is degraded (it can
    /// wait for a setting for days, and right after a restart it may still be restoring); only an execution that owns
    /// the batteries but stopped reporting is unhealthy.
    /// </summary>
    private static HealthComponentDto BuildJointExecutionComponent(JointBatteryExecutionSnapshot? execution, DateTimeOffset nowUtc)
    {
        if (execution is null) return new HealthComponentDto("Healthy", "Gemeinsame Ausführung ist nicht eingerichtet.", null);
        DateTimeOffset? updatedAt = execution.UpdatedAtUtc == DateTimeOffset.MinValue ? null : execution.UpdatedAtUtc;
        if (execution.OwnsControl && nowUtc - execution.UpdatedAtUtc > JointExecutionStaleAfter)
            return new HealthComponentDto("Unhealthy", "Gemeinsame Ausführung steuert die Akkus, meldet sich aber nicht mehr.", updatedAt);
        return execution.Status switch
        {
            "Disabled" or "Shadow" => new HealthComponentDto("Healthy", "Gemeinsame Ausführung ist nicht aktiv.", updatedAt),
            "Blocked" => new HealthComponentDto("Degraded", "Gemeinsame Ausführung wartet; Grund im Dashboard.", updatedAt),
            "Restoring" => new HealthComponentDto("Degraded", "Rücknahme der gemeinsamen Steuerung läuft.", updatedAt),
            _ => new HealthComponentDto("Healthy", "Gemeinsame Ausführung läuft.", updatedAt)
        };
    }

    private static string BuildOverallStatus(params string[] componentStatuses)
    {
        if (componentStatuses.Contains("Unhealthy", StringComparer.Ordinal)) return "Unhealthy";
        if (componentStatuses.Any(status => status is "Degraded" or "Starting")) return "Degraded";
        return "Healthy";
    }
}

public sealed record HealthResponseDto(
    string Status,
    DateTimeOffset CheckedAtUtc,
    HealthComponentDto Mqtt,
    HealthComponentDto Worker,
    HealthComponentDto JointExecution);

public sealed record HealthComponentDto(
    string Status,
    string Message,
    DateTimeOffset? LastUpdatedAtUtc);
