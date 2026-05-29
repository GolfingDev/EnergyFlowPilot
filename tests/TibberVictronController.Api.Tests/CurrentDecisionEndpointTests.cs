using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Decision;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class CurrentDecisionEndpointTests
{
    [Fact]
    public async Task GetCurrentDecisionAsyncReturnsLatestDecisionLogDto()
    {
        var logRepository = new FakeDecisionLogRepository(new[]
        {
            new DecisionLogEntry(
                Guid.NewGuid(),
                new DateTimeOffset(2026, 5, 2, 10, 5, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 2, 10, 5, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 2, 11, 0, 0, TimeSpan.Zero),
                new CurrentBatteryDecision(
                    new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                    1200),
                55m,
                0.18m,
                "EUR",
                800,
                0,
                "{}",
                new[] { new BatteryDecisionReason("Rule", "Begruendung") })
        });

        var result = await CurrentDecisionEndpoints.GetCurrentDecisionAsync(logRepository, CancellationToken.None);

        var okResult = Assert.IsType<Ok<CurrentBatteryDecisionResponseDto>>(result);
        Assert.Equal("Charge", okResult.Value!.DecisionState);
        Assert.Equal("Grid", okResult.Value.ChargeSource);
        Assert.Equal(1200, okResult.Value.TargetPowerWatts);
        Assert.Equal(800, okResult.Value.CurrentGridImportWatts);
    }

    [Fact]
    public async Task GetCurrentDecisionAsyncReturnsNotFoundWithoutDecisionLogs()
    {
        var logRepository = new FakeDecisionLogRepository(Array.Empty<DecisionLogEntry>());

        var result = await CurrentDecisionEndpoints.GetCurrentDecisionAsync(logRepository, CancellationToken.None);

        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public async Task GetDecisionLogsAsyncReturnsRecentDecisionLogs()
    {
        var logRepository = new FakeDecisionLogRepository(new[]
        {
            new DecisionLogEntry(
                Guid.NewGuid(),
                new DateTimeOffset(2026, 5, 2, 10, 5, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 2, 10, 5, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 2, 10, 20, 0, TimeSpan.Zero),
                new CurrentBatteryDecision(
                    new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
                    1793),
                88m,
                0.17m,
                "EUR",
                null,
                1793,
                "{}",
                new[] { new BatteryDecisionReason("ABSORB_GRID_EXPORT", "Aktueller Netzexport wird geladen.") })
        });

        var result = await CurrentDecisionEndpoints.GetDecisionLogsAsync(logRepository, 20, CancellationToken.None);

        var okResult = Assert.IsType<Ok<DecisionLogEntryResponseDto[]>>(result);
        var value = Assert.IsType<DecisionLogEntryResponseDto[]>(okResult.Value);
        Assert.Single(value);
        Assert.Equal("Charge", value[0].DecisionState);
        Assert.Equal("PV", value[0].ChargeSource);
    }

    private sealed class FakeDecisionLogRepository : IDecisionLogRepository
    {
        private readonly IReadOnlyList<DecisionLogEntry> entries;

        public FakeDecisionLogRepository(IReadOnlyList<DecisionLogEntry> entries)
        {
            this.entries = entries;
        }

        public Task SaveDecisionAsync(DecisionLogEntry decisionLogEntry, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<DecisionLogEntry>> GetRecentDecisionsAsync(int maxCount, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DecisionLogEntry>>(entries.Take(maxCount).ToArray());
        }

        public Task<int> DeleteDecisionsOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
