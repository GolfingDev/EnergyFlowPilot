using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Savings;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class SavingsEndpointTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSavingsAsyncReturnsMonthlySavings()
    {
        var repository = new FakeBatterySavingsRepository(new[]
        {
            CreateDailySummary(new DateOnly(2026, 5, 3), netSavings: 0.25m),
            CreateDailySummary(new DateOnly(2026, 5, 4), netSavings: 0.75m)
        });

        var result = await SavingsEndpoints.GetSavingsAsync(
            period: "month",
            referenceDate: new DateOnly(2026, 5, 15),
            currency: "EUR",
            repository,
            new NoopBatterySavingsAccountingService(),
            CancellationToken.None);

        var okResult = Assert.IsType<Ok<BatterySavingsResponseDto>>(result);
        Assert.Equal(new DateOnly(2026, 5, 1), okResult.Value!.StartDate);
        Assert.Equal(new DateOnly(2026, 5, 31), okResult.Value.EndDate);
        Assert.Equal(1.00m, okResult.Value.Aggregate.NetSavings);
        Assert.Equal(2, okResult.Value.DailySummaries.Count);
    }

    [Fact]
    public async Task GetSavingsAsyncReturnsBadRequestForInvalidPeriod()
    {
        var repository = new FakeBatterySavingsRepository(Array.Empty<BatterySavingsDailySummary>());

        var result = await SavingsEndpoints.GetSavingsAsync(
            period: "quarter",
            referenceDate: new DateOnly(2026, 5, 15),
            currency: "EUR",
            repository,
            new NoopBatterySavingsAccountingService(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<BatterySavingsErrorDto>>(result);
        Assert.Contains("day, week, month, year oder total", badRequest.Value!.Message);
    }

    [Fact]
    public async Task GetSavingsAsyncRequiresReferenceDateForBoundedPeriods()
    {
        var repository = new FakeBatterySavingsRepository(Array.Empty<BatterySavingsDailySummary>());

        var result = await SavingsEndpoints.GetSavingsAsync(
            period: "week",
            referenceDate: null,
            currency: "EUR",
            repository,
            new NoopBatterySavingsAccountingService(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<BatterySavingsErrorDto>>(result);
        Assert.Contains("Referenzdatum", badRequest.Value!.Message);
    }

    [Fact]
    public async Task GetSavingsAsyncAllowsTotalWithoutReferenceDate()
    {
        var repository = new FakeBatterySavingsRepository(new[]
        {
            CreateDailySummary(new DateOnly(2026, 5, 3), netSavings: 0.25m)
        });

        var result = await SavingsEndpoints.GetSavingsAsync(
            period: "total",
            referenceDate: null,
            currency: null,
            repository,
            new NoopBatterySavingsAccountingService(),
            CancellationToken.None);

        var okResult = Assert.IsType<Ok<BatterySavingsResponseDto>>(result);
        Assert.Equal("EUR", okResult.Value!.Currency);
        Assert.Equal(DateOnly.MinValue, okResult.Value.StartDate);
        Assert.Equal(DateOnly.MaxValue, okResult.Value.EndDate);
    }

    [Fact]
    public async Task CombinedResponseContainsBothScopesAndMissingDataIsNotZero()
    {
        var day = new DateOnly(2026, 5, 3);
        var primary = CreateDailySummary(day, .25m);
        var secondary = CreateDailySummary(day, .75m) with { BatteryId = "e3dc", MeasuredSeconds = 3600 };
        var result = await SavingsEndpoints.GetSavingsAsync("month", day, "EUR",
            new FakeBatterySavingsRepository([primary, secondary]), new NoopBatterySavingsAccountingService(), CancellationToken.None);
        var value = Assert.IsType<Ok<BatterySavingsResponseDto>>(result).Value!;
        Assert.Equal(1m, value.Aggregate.NetSavings);
        Assert.Equal(.75m, value.Batteries.Single(b => b.BatteryId == "e3dc").Aggregate!.NetSavings);
        Assert.Equal(1, value.Batteries.Single(b => b.BatteryId == "e3dc").MeasuredHours);
        var missing = BatterySavingsDtoMapper.Map(new BatterySavingsDtoMappingInput { DailySummaries = [primary] });
        Assert.Null(missing.Batteries.Single(b => b.BatteryId == "e3dc").Aggregate);
    }

    private static BatterySavingsDailySummary CreateDailySummary(
        DateOnly accountingDate,
        decimal netSavings)
    {
        var values = new BatterySavingsDailySummaryValues
        {
            AccountingDate = accountingDate,
            Currency = "EUR",
            GridChargedEnergyKwh = 1m,
            GridChargeCost = 0.10m,
            DischargedEnergyKwh = 1m,
            DischargeAvoidedCost = netSavings + 0.10m,
            NetSavings = netSavings,
            UpdatedAtUtc = UpdatedAtUtc
        };

        return new BatterySavingsDailySummary(values);
    }

    private sealed class FakeBatterySavingsRepository : IBatterySavingsRepository
    {
        private readonly IReadOnlyList<BatterySavingsDailySummary> dailySummaries;

        public FakeBatterySavingsRepository(IReadOnlyList<BatterySavingsDailySummary> dailySummaries)
        {
            this.dailySummaries = dailySummaries;
        }

        public Task SaveDailySummaryAsync(
            BatterySavingsDailySummary summary,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BatterySavingsDailySummary>> GetDailySummariesAsync(
            BatterySavingsQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var summariesInRange = dailySummaries
                .Where(summary =>
                    summary.Currency == query.Currency &&
                    summary.AccountingDate >= query.StartDate &&
                    summary.AccountingDate <= query.EndDate)
                .ToArray();

            return Task.FromResult<IReadOnlyList<BatterySavingsDailySummary>>(summariesInRange);
        }

        public Task<BatterySavingsAggregate> GetAggregateAsync(
            BatterySavingsQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(BatterySavingsAggregate.FromDailySummaries(dailySummaries));
        }
    }

    private sealed class NoopBatterySavingsAccountingService : IBatterySavingsAccountingService
    {
        public Task RefreshAsync(BatterySavingsQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        public Task RefreshRecentDaysAsync(int dayCount, string currency, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }
}
