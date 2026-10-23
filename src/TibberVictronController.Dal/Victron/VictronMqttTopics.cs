namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Contains fully resolved Victron MQTT topics for subscriptions and future writes.
/// </summary>
public sealed class VictronMqttTopics
{
    public required string GridPowerTopic { get; init; }

    public required string BatterySocTopic { get; init; }

    public required string BatteryPowerTopic { get; init; }

    /// <summary>
    /// Gets all topics whose values are summed to the house consumption, e.g. one topic per phase.
    /// </summary>
    public required IReadOnlyList<string> HouseConsumptionTopics { get; init; }

    /// <summary>
    /// Gets the leading house consumption topic; its messages trigger persisting a live consumption sample.
    /// </summary>
    public string HouseConsumptionTopic => HouseConsumptionTopics[0];

    public required string ChargeDischargeSetpointTopic { get; init; }

    public required string Hub4ModeTopic { get; init; }

    public required string Hub4ModeReadTopic { get; init; }

    public required string VebusMaxChargeCurrentTopic { get; init; }

    public required string BatteryVoltageTopic { get; init; }

    public required IReadOnlyList<string> ExternalEssAcPowerSetpointTopics { get; init; }

    /// <summary>
    /// Gets the Multi's AC-out power per controlled phase. In External ESS the setpoint controls AC-in, and loads on AC-out
    /// (on this site the complete house phase L1) are supplied in addition, so they must be added to the setpoint.
    /// </summary>
    public required IReadOnlyList<string> AcOutPowerTopics { get; init; }

    public required string DisableChargeTopic { get; init; }

    public required string DisableChargeReadTopic { get; init; }

    public required string DisableFeedInTopic { get; init; }

    public required string DisableFeedInReadTopic { get; init; }

    public IReadOnlyList<string> ReadTopics => new[]
        {
            GridPowerTopic,
            BatterySocTopic,
            BatteryPowerTopic
        }
        .Concat(HouseConsumptionTopics)
        .Concat(AcOutPowerTopics)
        .Concat(AcOutPowerTopics.Select(topic => topic.Replace("/Ac/Out/", "/Ac/ActiveIn/", StringComparison.Ordinal)))
        .Concat(ExternalEssAcPowerSetpointTopics.Select(topic => topic.StartsWith("W/", StringComparison.Ordinal) ? "N/" + topic[2..] : topic))
        .Concat(GridPowerTopic.EndsWith("/Ac/Power", StringComparison.Ordinal)
            ? Enumerable.Range(1, 3).Select(phase => GridPowerTopic[..^"/Ac/Power".Length] + $"/Ac/L{phase}/Power") : [])
        .Concat(new[]
        {
            Hub4ModeReadTopic,
            VebusMaxChargeCurrentTopic,
            BatteryVoltageTopic,
            DisableChargeReadTopic,
            DisableFeedInReadTopic
        })
        .ToArray();
}
