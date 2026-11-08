using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Energy;

namespace TibberVictronController.Business.Tests;

public sealed class EnergyHistoryTests
{
    private static readonly DateTimeOffset Slot = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PowerIsIntegratedPerQuarterHourAndSplitAtTheBoundary()
    {
        // 2 kW import from 10:14 to 10:16 (one point, then the next): one minute in each quarter hour.
        var points = new[]
        {
            new EnergyPowerPoint(Slot.AddMinutes(14)) { GridWatts = 2000, PricePerKwh = 0.30m },
            new EnergyPowerPoint(Slot.AddMinutes(16)) { GridWatts = -1000, PricePerKwh = 0.30m }
        };

        var slots = EnergyQuarterHourCalculator.Calculate(points, EnergySampleSource.DecisionLog, TimeSpan.FromMinutes(2), Slot, Slot.AddMinutes(30), Slot);

        Assert.Equal(2, slots.Count);
        Assert.Equal(2.0 / 60, slots[0].GridImportKwh, 6);
        Assert.Equal(60, slots[0].GridCoveredSeconds);
        // Second quarter: one more minute of import, then two minutes of 1 kW export (held for the maximum gap).
        Assert.Equal(2.0 / 60, slots[1].GridImportKwh, 6);
        Assert.Equal(2.0 / 60, slots[1].GridExportKwh, 6);
        Assert.Equal(180, slots[1].GridCoveredSeconds);
    }

    [Fact]
    public void GapsLongerThanTheMaximumAreNotInterpolated()
    {
        var points = new[]
        {
            new EnergyPowerPoint(Slot) { GridWatts = 3600 },
            new EnergyPowerPoint(Slot.AddMinutes(10)) { GridWatts = 3600 }
        };

        var slot = Assert.Single(EnergyQuarterHourCalculator.Calculate(points, EnergySampleSource.LiveSamples, TimeSpan.FromSeconds(10), Slot,
            Slot.AddMinutes(15), Slot));

        // Two points held for 10 s each, not 10 minutes: 20 s of 3.6 kW.
        Assert.Equal(20, slot.GridCoveredSeconds);
        Assert.Equal(0.02, slot.GridImportKwh, 6);
    }

    [Fact]
    public void HouseBalanceCountsEveryFlowOnceAndNeedsAllOfThem()
    {
        // 1 kW import, 4 kW PV, Victron charging 2 kW, E3/DC discharging 0.5 kW: house = 1 + 4 - 2 + 0.5 = 3.5 kW.
        var full = new EnergyPowerPoint(Slot)
            { GridWatts = 1000, SupplementaryPvWatts = 4000, PrimaryBatteryWatts = 2000, SupplementaryBatteryWatts = -500, EvWatts = 0 };
        // Without the E3/DC battery the balance is unknown; the grid still counts.
        var partial = new EnergyPowerPoint(Slot.AddMinutes(1)) { GridWatts = 1000, SupplementaryPvWatts = 4000, PrimaryBatteryWatts = 2000 };

        var slot = Assert.Single(EnergyQuarterHourCalculator.Calculate([full, partial], EnergySampleSource.LiveSamples, TimeSpan.FromMinutes(1),
            Slot, Slot.AddMinutes(15), Slot));

        Assert.Equal(60, slot.BalanceCoveredSeconds);
        Assert.Equal(120, slot.GridCoveredSeconds);
        Assert.Equal(3.5 / 60, slot.HouseConsumptionKwh!.Value, 6);
        Assert.Equal(2.0 / 60, slot.PrimaryBatteryChargeKwh!.Value, 6);
        Assert.Equal(0.5 / 60, slot.SupplementaryBatteryDischargeKwh!.Value, 6);
        Assert.Equal(4.0 / 60, slot.PvProductionKwh!.Value, 6);
        Assert.Equal(2.0 / 60, slot.GridImportKwh, 6);
    }

    [Fact]
    public void NegativePricesGiveNegativeImportCostAndMissingPricesNoCost()
    {
        var priced = Assert.Single(EnergyQuarterHourCalculator.Calculate([new EnergyPowerPoint(Slot) { GridWatts = 4000, PricePerKwh = -0.05m }],
            EnergySampleSource.DecisionLog, TimeSpan.FromMinutes(15), Slot, Slot.AddMinutes(15), Slot));
        var unpriced = Assert.Single(EnergyQuarterHourCalculator.Calculate([new EnergyPowerPoint(Slot) { GridWatts = 4000 }],
            EnergySampleSource.DecisionLog, TimeSpan.FromMinutes(15), Slot, Slot.AddMinutes(15), Slot));

        Assert.Equal(-0.05m, priced.ImportCost!.Value);
        Assert.Null(unpriced.PricePerKwh);
        Assert.Null(unpriced.ImportCost);
        Assert.Equal(0, unpriced.PriceCoveredSeconds);
    }

    [Fact]
    public void MergeKeepsTheMoreCompleteGroupAndEqualCoverageTakesTheNewValue()
    {
        var stored = new EnergyQuarterHour(Slot)
        {
            GridCoveredSeconds = 900, GridImportKwh = 1.0, BalanceCoveredSeconds = 900, HouseConsumptionKwh = 2.0, PriceCoveredSeconds = 900, PricePerKwh = 0.3m
        };
        // Raw live samples already deleted: only the decision log is left, without balance.
        var poorer = new EnergyQuarterHour(Slot) { GridCoveredSeconds = 800, GridImportKwh = 0.9, PriceCoveredSeconds = 900, PricePerKwh = 0.31m };

        var merged = EnergyQuarterHourCalculator.Merge(stored, poorer);

        Assert.Equal(1.0, merged.GridImportKwh);
        Assert.Equal(2.0, merged.HouseConsumptionKwh);
        Assert.Equal(0.31m, merged.PricePerKwh);
    }

    [Fact]
    public async Task RecalculationIsIdempotentAndCombinesBothSources()
    {
        var reader = new FakeReader
        {
            Log = [new EnergyPowerPoint(Slot) { GridWatts = 1000, PricePerKwh = 0.30m }, new EnergyPowerPoint(Slot.AddMinutes(2)) { GridWatts = 1000, PricePerKwh = 0.30m }],
            Live = Enumerable.Range(0, 900).Select(second => new EnergyPowerPoint(Slot.AddSeconds(second))
                { GridWatts = 1000, SupplementaryPvWatts = 0, PrimaryBatteryWatts = 0, SupplementaryBatteryWatts = 0 }).ToArray()
        };
        var repository = new FakeRepository();
        var service = new EnergyHistoryService(new EnergyHistoryDependencies(reader, repository, new FixedClock(Slot.AddHours(1))));

        await service.RecalculateAsync(Slot, Slot.AddMinutes(15));
        var first = Assert.Single(repository.Stored.Values);
        await service.RecalculateAsync(Slot, Slot.AddMinutes(15));
        var second = Assert.Single(repository.Stored.Values);

        Assert.Equal(first, second);
        Assert.Equal(EnergySampleSource.LiveSamples, second.GridSource);
        Assert.Equal(900, second.GridCoveredSeconds);
        Assert.Equal(0.25, second.GridImportKwh, 6);
        Assert.Equal(0.25, second.HouseConsumptionKwh!.Value, 6);
        Assert.Equal(0.30m, second.PricePerKwh);
        Assert.Equal(0.075m, Math.Round(second.ImportCost!.Value, 6));

        // Later the live samples are deleted: the stored, more complete values stay.
        reader.Live = [];
        await service.RecalculateAsync(Slot, Slot.AddMinutes(15));
        Assert.Equal(0.25, repository.Stored.Values.Single().HouseConsumptionKwh!.Value, 6);
        Assert.Equal(900, repository.Stored.Values.Single().GridCoveredSeconds);
    }

    private sealed class FakeReader : IEnergyMeasurementReader
    {
        public IReadOnlyList<EnergyPowerPoint> Log { get; set; } = [];
        public IReadOnlyList<EnergyPowerPoint> Live { get; set; } = [];

        public Task<IReadOnlyList<EnergyPowerPoint>> GetDecisionLogPointsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EnergyPowerPoint>>(Log.Where(point => point.AtUtc >= fromUtc && point.AtUtc < toUtc).ToArray());

        public Task<IReadOnlyList<EnergyPowerPoint>> GetLivePointsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EnergyPowerPoint>>(Live.Where(point => point.AtUtc >= fromUtc && point.AtUtc < toUtc).ToArray());

        public Task<DateTimeOffset?> GetFirstMeasurementAtUtcAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(Log.FirstOrDefault()?.AtUtc);
    }

    private sealed class FakeRepository : IEnergyQuarterHourRepository
    {
        public Dictionary<DateTimeOffset, EnergyQuarterHour> Stored { get; } = [];

        public Task<IReadOnlyList<EnergyQuarterHour>> GetAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EnergyQuarterHour>>(Stored.Values.Where(slot => slot.SlotStartUtc >= fromUtc && slot.SlotStartUtc < toUtc).ToArray());

        public Task<DateTimeOffset?> GetFirstBalanceSlotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored.Values.Where(slot => slot.BalanceCoveredSeconds > 0).Select(slot => (DateTimeOffset?)slot.SlotStartUtc).Min());

        public Task SaveAsync(IReadOnlyCollection<EnergyQuarterHour> quarterHours, CancellationToken cancellationToken = default)
        {
            foreach (var slot in quarterHours) Stored[slot.SlotStartUtc] = slot;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IUtcClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
