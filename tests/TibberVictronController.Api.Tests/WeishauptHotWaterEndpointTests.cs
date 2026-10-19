using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Weishaupt;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Api.Tests;

public sealed class WeishauptHotWaterEndpointTests
{
    [Fact]
    public async Task ReportExposesObservedTemperaturesAndLabelsCostEstimate()
    {
        var fake = new Data();
        var result = await WeishauptHotWaterEndpoints.GetReportAsync(new WeishauptHotWaterReportService(fake, fake, fake), default);
        var dto = Assert.IsType<Ok<HotWaterReportDto>>(result).Value!;
        Assert.Contains("keine gemessene", dto.CostAssessment);
        Assert.Equal(47m, Assert.Single(dto.Samples).TemperatureCelsius);
        var episode = Assert.Single(dto.Episodes);
        Assert.Null(episode.BridgedToNeed);
        Assert.True(episode.HasDataGaps);
        Assert.Equal("Preisfenster", episode.Trigger);
        Assert.Equal(0.1125m, dto.Plan?.EstimatedAdvantagePerKwh);
    }

    private sealed class Data : IWeishauptHotWaterStateStore, IWeishauptBiblockTelemetryRepository, IUtcClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 19, 16, 0, 0, TimeSpan.Zero);
        public Task<WeishauptHotWaterState> LoadAsync(CancellationToken ct)
        {
            var plan = new WeishauptHotWaterPricePlan(UtcNow, UtcNow.AddHours(-4), UtcNow.AddHours(-3), 0.25m, 0.40m, 0.1125m, 6m, 60, "Preisfenster");
            return Task.FromResult(new WeishauptHotWaterState { LastPlan = plan, Episodes = [new WeishauptHotWaterEpisode
                { StartedAtUtc = UtcNow.AddHours(-4), OriginalCelsius = 45m, RaisedCelsius = 50m,
                  Plan = plan, Trigger = "Preisfenster", WriteConfirmed = true }] });
        }
        public Task<IReadOnlyList<WeishauptBiblockTelemetrySample>> GetSamplesAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WeishauptBiblockTelemetrySample>>([new()
            { MeasuredAtUtc = UtcNow, HotWaterTemperatureCelsius = 47m, OutdoorTemperatureCelsius = 10m, OperatingStatusCode = 19 }]);
        public Task SaveAsync(WeishauptHotWaterState state, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveSampleAsync(WeishauptBiblockTelemetrySample sample, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset threshold, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
