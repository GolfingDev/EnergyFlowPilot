using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Homematic;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Tests.TestDoubles;

namespace TibberVictronController.Business.Tests;

public sealed class HomematicClimateHistoryServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetHistoryAsyncAveragesSamplesPerBucketAndIgnoresMissingValues()
    {
        var service = CreateService(
            Sample(HomematicClimateSampleKind.HeatingGroup, "INT0000003:1", "HmIP-HEATING", "Heizung Bad INT0000003", NowUtc.AddMinutes(-55), actual: 21.0m, setPoint: null),
            Sample(HomematicClimateSampleKind.HeatingGroup, "INT0000003:1", "HmIP-HEATING", "Heizung Bad INT0000003", NowUtc.AddMinutes(-40), actual: 21.4m, setPoint: 22m));

        var history = await service.GetHistoryAsync(HomematicHistoryRange.Week);

        Assert.Equal(TimeSpan.FromMinutes(30), history.BucketDuration);
        Assert.Equal(NowUtc.AddDays(-7), history.StartsAtUtc);
        var series = Assert.Single(history.Series);
        Assert.Equal("Heizung Bad", series.DisplayName);
        var point = Assert.Single(series.Points);
        Assert.Equal(NowUtc.AddHours(-1), point.BucketStartsAtUtc);
        Assert.Equal(21.2m, point.ActualTemperatureCelsius);
        Assert.Equal(22m, point.SetPointTemperatureCelsius);
        Assert.Null(point.ValveOpeningPercent);
    }

    [Fact]
    public async Task GetHistoryAsyncSummarizesLatestPollWithoutCountingRoomsTwice()
    {
        var latest = NowUtc.AddMinutes(-5);
        var service = CreateService(CreateInstallationSnapshot(latest)
            .Append(Sample(HomematicClimateSampleKind.WeatherStation, "SWO:1", "HmIP-SWO-B", "Wetterstation", latest.AddMinutes(-5), actual: 9.0m))
            .ToArray());

        var history = await service.GetHistoryAsync(HomematicHistoryRange.Day);

        Assert.Equal(latest, history.LastPolledAtUtc);
        Assert.Equal(10.1m, history.Summary.OutdoorTemperatureCelsius);
        // Radiator thermostats: 80 % (Bad), 0 % (Buero), 0 % (Schlafzimmer, switched off).
        Assert.Equal(26.7m, history.Summary.RadiatorValveOpeningPercent);
        Assert.Equal(25m, history.Summary.UnderfloorValveOpeningPercent);
        // Rooms: groups Bad + Wohnzimmer, BidCos Buero + Schlafzimmer wall thermostat. The HmIP thermostats belong to groups.
        Assert.Equal(4, history.Summary.RoomCount);
        Assert.Equal(1, history.Summary.RoomsBelowSetPointCount);
        Assert.False(history.Series.Single(series => series.ChannelAddress == "ETRV-BAD:1").IsRoom);
        Assert.True(history.Series.Single(series => series.ChannelAddress == "RTDN-BUERO:4").IsRoom);
        Assert.Equal("Esszimmer", history.Series.Single(series => series.ChannelAddress == "FAL:1").DisplayName);
    }

    [Fact]
    public async Task GetHistoryAsyncPairsOutdoorTemperatureWithValveOpeningPerCircuit()
    {
        var latest = NowUtc.AddMinutes(-5);
        var service = CreateService(CreateInstallationSnapshot(latest)
            .Append(Sample(HomematicClimateSampleKind.WeatherStation, "SWO:1", "HmIP-SWO-B", "Wetterstation", latest.AddMinutes(-5), actual: 9.0m))
            .ToArray());

        var history = await service.GetHistoryAsync(HomematicHistoryRange.Day);

        // The older bucket has only an outdoor temperature and therefore no pair.
        var point = Assert.Single(history.ValveVersusOutdoor);
        Assert.Equal(latest, point.BucketStartsAtUtc);
        Assert.Equal(10.1m, point.OutdoorTemperatureCelsius);
        Assert.Equal(26.7m, point.RadiatorValveOpeningPercent);
        Assert.Equal(25m, point.UnderfloorValveOpeningPercent);
    }

    [Fact]
    public async Task GetHistoryAsyncReportsEmptyHistoryAndCollectionState()
    {
        var settingsStore = new FakeControllerSettingStore();
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.HomematicEnabledKey, "true", ControllerSettingSensitivity.Normal, NowUtc));
        var service = new HomematicClimateHistoryService(new FakeHomematicClimateRepository([]), settingsStore, new FixedUtcClock(NowUtc));

        var history = await service.GetHistoryAsync(HomematicHistoryRange.Month);

        Assert.True(history.IsCollectionEnabled);
        Assert.Null(history.LastPolledAtUtc);
        Assert.Empty(history.Series);
        Assert.Empty(history.ValveVersusOutdoor);
        Assert.Equal(0, history.Summary.RoomCount);
        Assert.Equal(TimeSpan.FromHours(2), history.BucketDuration);
    }

    [Fact]
    public void IsRoomTreatsSingleHmIpThermostatsAsRoomsOnlyWithoutHeatingGroups()
    {
        Assert.False(HomematicClimateHistoryService.IsRoom(HomematicClimateSampleKind.RadiatorThermostat, "HmIP-eTRV-3", hasHeatingGroups: true));
        Assert.True(HomematicClimateHistoryService.IsRoom(HomematicClimateSampleKind.RadiatorThermostat, "HmIP-eTRV-3", hasHeatingGroups: false));
        Assert.True(HomematicClimateHistoryService.IsRoom(HomematicClimateSampleKind.WallThermostat, "HM-TC-IT-WM-W-EU", hasHeatingGroups: true));
        Assert.False(HomematicClimateHistoryService.IsRoom(HomematicClimateSampleKind.UnderfloorHeatingZone, "HmIP-FALMOT-C12", hasHeatingGroups: false));
    }

    private static IEnumerable<HomematicClimateSample> CreateInstallationSnapshot(DateTimeOffset polledAtUtc)
    {
        yield return Sample(HomematicClimateSampleKind.HeatingGroup, "INT0000003:1", "HmIP-HEATING", "Heizung Bad INT0000003", polledAtUtc, actual: 21.0m, setPoint: 22m, valve: 80m);
        yield return Sample(HomematicClimateSampleKind.HeatingGroup, "INT0000004:1", "HmIP-HEATING", "Heizung Wohnzimmer INT0000004", polledAtUtc, actual: 21.7m, setPoint: 21.5m, valve: 0m);
        yield return Sample(HomematicClimateSampleKind.RadiatorThermostat, "ETRV-BAD:1", "HmIP-eTRV-3", "Heizung Bad", polledAtUtc, actual: 21.0m, setPoint: 22m, valve: 80m);
        yield return Sample(HomematicClimateSampleKind.RadiatorThermostat, "ETRV-SCHLAF:1", "HmIP-eTRV-3", "Heizung Schlafzimmer", polledAtUtc, actual: 15.1m, setPoint: 4.5m, valve: 0m);
        yield return Sample(HomematicClimateSampleKind.RadiatorThermostat, "RTDN-BUERO:4", "HM-CC-RT-DN", "Heizung Buero", polledAtUtc, actual: 20.7m, setPoint: 19m, valve: 0m);
        yield return Sample(HomematicClimateSampleKind.WallThermostat, "WTH-BAD:1", "HmIP-WTH-B-2", "Thermostat Bad", polledAtUtc, actual: 21.0m, setPoint: 22m);
        yield return Sample(HomematicClimateSampleKind.WallThermostat, "TC-SCHLAF:2", "HM-TC-IT-WM-W-EU", "Thermostat Schlafzimmer", polledAtUtc, actual: 18.3m, setPoint: 17m);
        yield return Sample(HomematicClimateSampleKind.UnderfloorHeatingZone, "FAL:1", "HmIP-FALMOT-C12", "Steuerung Fussbodenheizung", polledAtUtc, valve: 50m, channelName: "Esszimmer");
        yield return Sample(HomematicClimateSampleKind.UnderfloorHeatingZone, "FAL:5", "HmIP-FALMOT-C12", "Steuerung Fussbodenheizung", polledAtUtc, valve: 0m, channelName: "Flur");
        yield return Sample(HomematicClimateSampleKind.WeatherStation, "SWO:1", "HmIP-SWO-B", "Wetterstation", polledAtUtc, actual: 10.1m);
    }

    private static HomematicClimateSample Sample(
        HomematicClimateSampleKind kind,
        string channelAddress,
        string deviceType,
        string deviceName,
        DateTimeOffset polledAtUtc,
        decimal? actual = null,
        decimal? setPoint = null,
        decimal? valve = null,
        string? channelName = null)
    {
        return new HomematicClimateSample
        {
            PolledAtUtc = polledAtUtc,
            ChannelAddress = channelAddress,
            DeviceType = deviceType,
            DeviceName = deviceName,
            ChannelName = channelName ?? $"{deviceName}:1",
            Kind = kind,
            ActualTemperatureCelsius = actual,
            SetPointTemperatureCelsius = setPoint,
            ValveOpeningPercent = valve,
            IsReachable = true
        };
    }

    private static HomematicClimateHistoryService CreateService(params HomematicClimateSample[] samples)
    {
        return new HomematicClimateHistoryService(
            new FakeHomematicClimateRepository(samples),
            new FakeControllerSettingStore(),
            new FixedUtcClock(NowUtc));
    }

    private sealed class FakeHomematicClimateRepository : IHomematicClimateRepository
    {
        private readonly IReadOnlyList<HomematicClimateSample> samples;

        public FakeHomematicClimateRepository(IReadOnlyList<HomematicClimateSample> samples)
        {
            this.samples = samples;
        }

        public Task SaveSamplesAsync(IReadOnlyList<HomematicClimateSample> samplesToSave, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<HomematicClimateSample>> GetSamplesAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<HomematicClimateSample>>(samples
                .Where(sample => sample.PolledAtUtc >= startsAtUtc && sample.PolledAtUtc < endsAtUtc)
                .ToArray());
        }

        public Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedUtcClock : IUtcClock
    {
        public FixedUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
