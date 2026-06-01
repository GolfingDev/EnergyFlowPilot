using System.Collections.Concurrent;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Detects input changes large enough to justify recalculating the current battery decision immediately.
/// </summary>
public sealed class SignificantTelemetryChangeDetector
{
    private const decimal AbsolutePowerThresholdWatts = 300m;
    private const decimal RelativePowerThresholdPercent = 10m;
    private const decimal RelativePowerMinimumDeltaWatts = 100m;
    private const decimal SocThresholdPercent = 0.5m;

    private readonly ConcurrentDictionary<string, decimal> lastValuesByTopic = new(StringComparer.Ordinal);

    public bool ShouldTrigger(string topic, decimal value, TelemetrySignalKind signalKind)
    {
        var hadPreviousValue = lastValuesByTopic.TryGetValue(topic, out var previousValue);
        lastValuesByTopic[topic] = value;

        if (!hadPreviousValue)
        {
            return false;
        }

        return signalKind == TelemetrySignalKind.StateOfCharge
            ? IsSignificantStateOfChargeChange(previousValue, value)
            : IsSignificantPowerChange(previousValue, value);
    }

    public static bool IsSignificantPowerChange(decimal previousValue, decimal currentValue)
    {
        var absoluteDelta = Math.Abs(currentValue - previousValue);
        if (absoluteDelta >= AbsolutePowerThresholdWatts)
        {
            return true;
        }

        var baseline = Math.Max(Math.Abs(previousValue), 1m);
        var relativeDeltaPercent = absoluteDelta / baseline * 100m;

        return absoluteDelta >= RelativePowerMinimumDeltaWatts &&
            relativeDeltaPercent >= RelativePowerThresholdPercent;
    }

    public static bool IsSignificantStateOfChargeChange(decimal previousValue, decimal currentValue)
    {
        return Math.Abs(currentValue - previousValue) >= SocThresholdPercent;
    }
}

public enum TelemetrySignalKind
{
    Power,
    StateOfCharge
}
