using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Decision;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class CurrentDecisionEndpointTests
{
    [Fact]
    public async Task GetCurrentDecisionAsyncReturnsDecisionDto()
    {
        var service = new FakeCurrentBatteryDecisionService(new CurrentBatteryDecisionResult(
            decidedAtUtc: new DateTimeOffset(2026, 5, 2, 10, 5, 0, TimeSpan.Zero),
            validFromUtc: new DateTimeOffset(2026, 5, 2, 10, 5, 0, TimeSpan.Zero),
            validToUtc: new DateTimeOffset(2026, 5, 2, 11, 0, 0, TimeSpan.Zero),
            decision: new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                targetPowerWatts: 1200),
            batteryState: new BatteryState(55m, new DateTimeOffset(2026, 5, 2, 10, 4, 0, TimeSpan.Zero)),
            siteTelemetry: new CurrentSiteTelemetry(800, 300, new DateTimeOffset(2026, 5, 2, 10, 5, 0, TimeSpan.Zero)),
            tibberPricePerKwh: 0.18m,
            tibberPriceCurrency: "EUR",
            reasons: new[] { new BatteryDecisionReason("Rule", "Begruendung") },
            inputSummaryJson: "{}"));

        var result = await CurrentDecisionEndpoints.GetCurrentDecisionAsync(service, CancellationToken.None);

        var okResult = Assert.IsType<Ok<CurrentBatteryDecisionResponseDto>>(result);
        Assert.Equal("Charge", okResult.Value!.DecisionState);
        Assert.Equal("Grid", okResult.Value.ChargeSource);
        Assert.Equal(1200, okResult.Value.TargetPowerWatts);
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
        Assert.Single(okResult.Value!);
        Assert.Equal("Charge", okResult.Value[0].DecisionState);
        Assert.Equal("PV", okResult.Value[0].ChargeSource);
    }

    private sealed class FakeCurrentBatteryDecisionService : ICurrentBatteryDecisionService
    {
        private readonly CurrentBatteryDecisionResult result;

        public FakeCurrentBatteryDecisionService(CurrentBatteryDecisionResult result)
        {
            this.result = result;
        }

        public Task<CurrentBatteryDecisionResult> CalculateCurrentDecisionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
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
