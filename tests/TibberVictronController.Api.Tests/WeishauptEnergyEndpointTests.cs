using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Weishaupt;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Api.Tests;

public sealed class WeishauptEnergyEndpointTests
{
    [Fact]
    public async Task DefaultRangeExposesBothWeeksWithExplicitMissingValues()
    {
        var data = new Data();
        var result = await WeishauptEnergyEndpoints.GetAsync(new(data, data), null, null, default);
        var dto = Assert.IsType<Ok<WeishauptEnergyHistoryDto>>(result).Value!;
        Assert.Equal(168, dto.Hours.Count);
        Assert.Equal(168, dto.PreviousHours.Count);
        Assert.All(dto.Hours, h => Assert.Null(h.ElectricalKwh));
        Assert.Equal(dto.StartsAtUtc.AddDays(-7), dto.PreviousHours[0].StartsAtUtc);
    }

    [Fact]
    public async Task InvalidRangeReturnsBadRequest()
    {
        var data = new Data();
        var result = await WeishauptEnergyEndpoints.GetAsync(new(data, data), 99, null, default);
        Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
    }

    private sealed class Data : IWeishauptBiblockTelemetryRepository, IUtcClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        public Task<IReadOnlyList<WeishauptBiblockTelemetrySample>> GetSamplesAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WeishauptBiblockTelemetrySample>>([]);
        public Task SaveSampleAsync(WeishauptBiblockTelemetrySample sample, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset threshold, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
