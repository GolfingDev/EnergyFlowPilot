using System.Text.RegularExpressions;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Homematic;

/// <summary>
/// Aggregates raw 5-minute Homematic samples into chart-sized buckets. Without bucketing a 30-day view
/// would send about 150,000 points to the browser.
/// </summary>
public sealed partial class HomematicClimateHistoryService : IHomematicClimateHistoryService
{
    // Thermostats use 4.5 °C as "off"; such rooms must not count as too cold.
    private const decimal SwitchedOffSetPointCelsius = 5m;
    // Small deviations are normal control behaviour and should not flag a room.
    private const decimal BelowSetPointToleranceKelvin = 0.5m;

    private readonly IHomematicClimateRepository repository;
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly IUtcClock utcClock;

    public HomematicClimateHistoryService(
        IHomematicClimateRepository repository,
        IControllerSettingStore controllerSettingStore,
        IUtcClock utcClock)
    {
        this.repository = repository;
        this.controllerSettingStore = controllerSettingStore;
        this.utcClock = utcClock;
    }

    public async Task<HomematicClimateHistory> GetHistoryAsync(HomematicHistoryRange range, CancellationToken cancellationToken = default)
    {
        var (duration, bucketDuration) = GetWindow(range);
        var endsAtUtc = utcClock.UtcNow.ToUniversalTime();
        var startsAtUtc = endsAtUtc - duration;
        var samples = await repository.GetSamplesAsync(startsAtUtc, endsAtUtc, cancellationToken);
        var hasHeatingGroups = samples.Any(sample => sample.Kind == HomematicClimateSampleKind.HeatingGroup);

        return new HomematicClimateHistory
        {
            StartsAtUtc = startsAtUtc,
            EndsAtUtc = endsAtUtc,
            BucketDuration = bucketDuration,
            IsCollectionEnabled = await IsCollectionEnabledAsync(cancellationToken),
            LastPolledAtUtc = samples.Count == 0 ? null : samples.Max(sample => sample.PolledAtUtc),
            Summary = CreateSummary(samples, hasHeatingGroups),
            Series = samples
                .GroupBy(sample => sample.ChannelAddress, StringComparer.Ordinal)
                .Select(channelSamples => CreateSeries(channelSamples.ToArray(), bucketDuration, hasHeatingGroups))
                .OrderBy(series => series.Kind)
                .ThenBy(series => series.DisplayName, StringComparer.CurrentCulture)
                .ToArray(),
            ValveVersusOutdoor = CreateValveVersusOutdoor(samples, bucketDuration)
        };
    }

    /// <summary>
    /// Decides whether a channel stands for a room. The XML-API exposes no group membership, so the rule relies on
    /// a technical fact: HmIP heating groups only contain HmIP devices, BidCos thermostats ("HM-*") always regulate on
    /// their own. Single HmIP thermostats therefore only count as rooms when no heating group exists.
    /// </summary>
    public static bool IsRoom(HomematicClimateSampleKind kind, string deviceType, bool hasHeatingGroups)
    {
        return kind switch
        {
            HomematicClimateSampleKind.HeatingGroup => true,
            HomematicClimateSampleKind.RadiatorThermostat or HomematicClimateSampleKind.WallThermostat =>
                deviceType.StartsWith("HM-", StringComparison.OrdinalIgnoreCase) || !hasHeatingGroups,
            _ => false
        };
    }

    private static (TimeSpan Duration, TimeSpan BucketDuration) GetWindow(HomematicHistoryRange range)
    {
        return range switch
        {
            HomematicHistoryRange.Day => (TimeSpan.FromDays(1), TimeSpan.FromMinutes(5)),
            HomematicHistoryRange.Week => (TimeSpan.FromDays(7), TimeSpan.FromMinutes(30)),
            HomematicHistoryRange.Month => (TimeSpan.FromDays(30), TimeSpan.FromHours(2)),
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Unbekannter Zeitraum fuer den Homematic-Verlauf.")
        };
    }

    private async Task<bool> IsCollectionEnabledAsync(CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(ControllerSettingDefaults.HomematicEnabledKey, cancellationToken);

        return setting is { IsConfigured: true } && bool.TryParse(setting.Value, out var isEnabled) && isEnabled;
    }

    private static HomematicClimateSummary CreateSummary(IReadOnlyList<HomematicClimateSample> samples, bool hasHeatingGroups)
    {
        if (samples.Count == 0)
        {
            return new HomematicClimateSummary { RoomCount = 0, RoomsBelowSetPointCount = 0 };
        }

        var lastPolledAtUtc = samples.Max(sample => sample.PolledAtUtc);
        var latestSamples = samples.Where(sample => sample.PolledAtUtc == lastPolledAtUtc).ToArray();
        var heatedRooms = latestSamples
            .Where(sample => IsRoom(sample.Kind, sample.DeviceType, hasHeatingGroups) &&
                sample.ActualTemperatureCelsius is not null &&
                sample.SetPointTemperatureCelsius > SwitchedOffSetPointCelsius)
            .ToArray();

        return new HomematicClimateSummary
        {
            OutdoorTemperatureCelsius = Average(latestSamples
                .Where(sample => sample.Kind == HomematicClimateSampleKind.WeatherStation)
                .Select(sample => sample.ActualTemperatureCelsius)),
            RadiatorValveOpeningPercent = Average(latestSamples
                .Where(sample => sample.Kind == HomematicClimateSampleKind.RadiatorThermostat)
                .Select(sample => sample.ValveOpeningPercent)),
            UnderfloorValveOpeningPercent = Average(latestSamples
                .Where(sample => sample.Kind == HomematicClimateSampleKind.UnderfloorHeatingZone)
                .Select(sample => sample.ValveOpeningPercent)),
            RoomCount = heatedRooms.Length,
            RoomsBelowSetPointCount = heatedRooms.Count(sample =>
                sample.ActualTemperatureCelsius < sample.SetPointTemperatureCelsius - BelowSetPointToleranceKelvin)
        };
    }

    private static HomematicClimateSeries CreateSeries(
        IReadOnlyList<HomematicClimateSample> channelSamples,
        TimeSpan bucketDuration,
        bool hasHeatingGroups)
    {
        // Names can be changed in the CCU; the newest sample carries the current one.
        var latestSample = channelSamples.MaxBy(sample => sample.PolledAtUtc)!;

        return new HomematicClimateSeries
        {
            ChannelAddress = latestSample.ChannelAddress,
            DisplayName = GetDisplayName(latestSample),
            Kind = latestSample.Kind,
            IsRoom = IsRoom(latestSample.Kind, latestSample.DeviceType, hasHeatingGroups),
            Points = channelSamples
                .GroupBy(sample => GetBucketStart(sample.PolledAtUtc, bucketDuration))
                .OrderBy(bucket => bucket.Key)
                .Select(bucket => new HomematicClimatePoint
                {
                    BucketStartsAtUtc = bucket.Key,
                    ActualTemperatureCelsius = Average(bucket.Select(sample => sample.ActualTemperatureCelsius)),
                    SetPointTemperatureCelsius = Average(bucket.Select(sample => sample.SetPointTemperatureCelsius)),
                    ValveOpeningPercent = Average(bucket.Select(sample => sample.ValveOpeningPercent))
                })
                .ToArray()
        };
    }

    private static IReadOnlyList<HomematicValveOutdoorPoint> CreateValveVersusOutdoor(
        IReadOnlyList<HomematicClimateSample> samples,
        TimeSpan bucketDuration)
    {
        var points = new List<HomematicValveOutdoorPoint>();

        foreach (var bucket in samples.GroupBy(sample => GetBucketStart(sample.PolledAtUtc, bucketDuration)).OrderBy(bucket => bucket.Key))
        {
            var outdoorTemperature = Average(bucket
                .Where(sample => sample.Kind == HomematicClimateSampleKind.WeatherStation)
                .Select(sample => sample.ActualTemperatureCelsius));
            var radiatorValve = Average(bucket
                .Where(sample => sample.Kind == HomematicClimateSampleKind.RadiatorThermostat)
                .Select(sample => sample.ValveOpeningPercent));
            var underfloorValve = Average(bucket
                .Where(sample => sample.Kind == HomematicClimateSampleKind.UnderfloorHeatingZone)
                .Select(sample => sample.ValveOpeningPercent));

            if (outdoorTemperature is null || (radiatorValve is null && underfloorValve is null))
            {
                continue;
            }

            points.Add(new HomematicValveOutdoorPoint
            {
                BucketStartsAtUtc = bucket.Key,
                OutdoorTemperatureCelsius = outdoorTemperature.Value,
                RadiatorValveOpeningPercent = radiatorValve,
                UnderfloorValveOpeningPercent = underfloorValve
            });
        }

        return points;
    }

    private static string GetDisplayName(HomematicClimateSample sample)
    {
        return sample.Kind switch
        {
            // Virtual groups carry their internal address in the name ("Heizung Bad INT0000003").
            HomematicClimateSampleKind.HeatingGroup => VirtualDeviceSuffix().Replace(sample.DeviceName, string.Empty),
            // Underfloor zones are named per channel ("Esszimmer"); the device name is the actuator.
            HomematicClimateSampleKind.UnderfloorHeatingZone => sample.ChannelName,
            _ => sample.DeviceName
        };
    }

    private static DateTimeOffset GetBucketStart(DateTimeOffset timestampUtc, TimeSpan bucketDuration)
    {
        var ticks = timestampUtc.UtcTicks;

        return new DateTimeOffset(ticks - ticks % bucketDuration.Ticks, TimeSpan.Zero);
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var presentValues = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();

        return presentValues.Length == 0 ? null : Math.Round(presentValues.Average(), 1);
    }

    [GeneratedRegex(@"\s+INT\d+$")]
    private static partial Regex VirtualDeviceSuffix();
}
