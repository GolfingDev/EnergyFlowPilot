using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Homematic;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class HomematicEndpointTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetHistoryAsyncMapsHistoryForRequestedRange()
    {
        var historyService = new FakeHomematicClimateHistoryService();

        var result = await HomematicEndpoints.GetHistoryAsync("week", historyService, CancellationToken.None);

        var okResult = Assert.IsType<Ok<HomematicHistoryResponseDto>>(result);
        Assert.Equal(HomematicHistoryRange.Week, historyService.RequestedRange);
        Assert.Equal(30, okResult.Value!.BucketMinutes);
        Assert.True(okResult.Value.IsCollectionEnabled);
        var series = Assert.Single(okResult.Value.Series);
        Assert.Equal("HeatingGroup", series.Kind);
        Assert.True(series.IsRoom);
        Assert.Equal(21.5m, Assert.Single(series.Points).ActualTemperatureCelsius);
        Assert.Equal(10.1m, Assert.Single(okResult.Value.ValveVersusOutdoor).OutdoorTemperatureCelsius);
        Assert.Equal(1, okResult.Value.Summary.RoomsBelowSetPointCount);
    }

    [Fact]
    public async Task GetHistoryAsyncRejectsUnknownRange()
    {
        var historyService = new FakeHomematicClimateHistoryService();

        var result = await HomematicEndpoints.GetHistoryAsync("year", historyService, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<HomematicErrorDto>>(result);
        Assert.Contains("day, week oder month", badRequest.Value!.Message, StringComparison.Ordinal);
        Assert.Null(historyService.RequestedRange);
    }

    private sealed class FakeHomematicClimateHistoryService : IHomematicClimateHistoryService
    {
        public HomematicHistoryRange? RequestedRange { get; private set; }

        public Task<HomematicClimateHistory> GetHistoryAsync(HomematicHistoryRange range, CancellationToken cancellationToken = default)
        {
            RequestedRange = range;

            return Task.FromResult(new HomematicClimateHistory
            {
                StartsAtUtc = NowUtc.AddDays(-7),
                EndsAtUtc = NowUtc,
                BucketDuration = TimeSpan.FromMinutes(30),
                IsCollectionEnabled = true,
                LastPolledAtUtc = NowUtc.AddMinutes(-5),
                Summary = new HomematicClimateSummary { RoomCount = 3, RoomsBelowSetPointCount = 1 },
                Series =
                [
                    new HomematicClimateSeries
                    {
                        ChannelAddress = "INT0000003:1",
                        DisplayName = "Heizung Bad",
                        Kind = HomematicClimateSampleKind.HeatingGroup,
                        IsRoom = true,
                        Points = [new HomematicClimatePoint { BucketStartsAtUtc = NowUtc.AddMinutes(-30), ActualTemperatureCelsius = 21.5m }]
                    }
                ],
                ValveVersusOutdoor =
                [
                    new HomematicValveOutdoorPoint { BucketStartsAtUtc = NowUtc.AddMinutes(-30), OutdoorTemperatureCelsius = 10.1m, RadiatorValveOpeningPercent = 40m }
                ]
            });
        }
    }
}
