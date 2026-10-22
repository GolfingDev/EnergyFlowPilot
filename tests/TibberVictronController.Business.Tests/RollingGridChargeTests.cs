using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Tests.TestDoubles;

namespace TibberVictronController.Business.Tests;

public sealed class RollingGridChargeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 10, 15, 0, TimeSpan.Zero);
    private static readonly BatteryConfiguration Configuration = new(new BatteryConfigurationValues
    {
        TotalCapacityKwh = 19.2m, MinimumStateOfChargePercent = 10m,
        PlanningMinimumStateOfChargePercent = 15m, PlanningMaximumStateOfChargePercent = 95m,
        MaximumChargePowerWatts = 4000, MaximumDischargePowerWatts = 4000,
        RoundTripEfficiencyPercent = 90m, MinimumSavingsPerKwh = .03m
    });

    [Fact]
    public async Task RecreatedServicesKeepMeasuredGridEnergyForTheMostExpensiveSlot()
    {
        var inputs = new ForecastInputs();
        var history = new History();
        var settings = new FakeControllerSettingStore();
        foreach (var setting in ControllerSettingDefaults.CreateDefaultSettings(Start))
            await settings.SaveSettingAsync(setting);
        var soc = 15m;
        var decisions = new List<CurrentBatteryDecision>();
        var efficiency = (decimal)Math.Sqrt(.9);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var now = Start.AddMinutes(15 * cycle);
            var battery = new FakeBatteryStateProvider(new BatteryState(soc, now));
            var config = new FakeBatteryConfigurationProvider(Configuration);
            // Each cycle gets fresh services, as with real request scopes or a process restart.
            var forecast = new BatteryForecastService(inputs, inputs, inputs, battery, config, settings, history);
            var service = new CurrentBatteryDecisionService(new CurrentBatteryDecisionServiceDependencies
            {
                UtcClock = new Clock(now), BatteryStateProvider = battery, BatteryConfigurationProvider = config,
                CurrentSiteTelemetryProvider = new Telemetry(new CurrentSiteTelemetry(
                    (int)(ForecastInputs.Loads[cycle] * 4000m), 0, now, 0)),
                TibberPriceForecastProvider = inputs, BatteryForecastService = forecast,
                ControllerSettingStore = settings, DecisionLogRepository = history
            });
            var result = await service.CalculateCurrentDecisionAsync();
            decisions.Add(result.Decision);
            var energy = result.Decision.TargetPowerWatts / 1000m * .25m;
            if (result.Decision.Instruction.DecisionState == BatteryDecisionState.Charge)
                soc += energy * efficiency / Configuration.TotalCapacityKwh * 100m;
            if (result.Decision.Instruction.DecisionState == BatteryDecisionState.Discharge)
                soc -= energy / efficiency / Configuration.TotalCapacityKwh * 100m;
            Assert.InRange(soc, 15m - .01m, 95m);
        }
        Assert.Equal(BatteryChargeSource.Grid, decisions[0].Instruction.ChargeSource);
        Assert.Equal(BatteryDecisionState.Idle, decisions[1].Instruction.DecisionState);
        Assert.Equal(BatteryDecisionState.Discharge, decisions[2].Instruction.DecisionState);
        using var saved = JsonDocument.Parse(history.Entries[1].InputSummaryJson);
        Assert.True(saved.RootElement.GetProperty("GridChargeEnergyCredit").GetProperty("EnergyKwh").GetDecimal() > .3m);
        var purchase = decisions[0].TargetPowerWatts / 4000m;
        var output = decisions[2].TargetPowerWatts / 4000m;
        Assert.True(output * ForecastInputs.Prices[2] - purchase * ForecastInputs.Prices[0] > purchase * .03m - .001m);
    }

    [Fact]
    public void CreditRequiresMeasuredGainAndExpiresOrResetsConservatively()
    {
        var initial = new BatteryState(15m, Start);
        var credit = GridChargeEnergyCredit.Update(null, initial, Configuration, "tibber");
        var command = Log(credit, BatteryChargeSource.Grid);
        Assert.Equal(0m, GridChargeEnergyCredit.Update(command, new BatteryState(15m, Start.AddMinutes(1)), Configuration, "tibber").EnergyKwh);
        var charged = GridChargeEnergyCredit.Update(command, new BatteryState(16m, Start.AddMinutes(15)), Configuration, "tibber");
        Assert.Equal(.192m, charged.EnergyKwh);
        Assert.Equal(0m, GridChargeEnergyCredit.Update(command, new BatteryState(16m, Start.AddHours(1)), Configuration, "tibber").EnergyKwh);
        Assert.Equal(0m, GridChargeEnergyCredit.Update(command, new BatteryState(16m, Start.AddMinutes(15)), Configuration, "octopusGermany").EnergyKwh);
        Assert.Equal(0m, GridChargeEnergyCredit.Update(Log(credit, BatteryChargeSource.PV),
            new BatteryState(16m, Start.AddMinutes(15)), Configuration, "tibber").EnergyKwh);
        Assert.Equal(0m, GridChargeEnergyCredit.Update(Log(credit, BatteryChargeSource.Grid, manual: true),
            new BatteryState(16m, Start.AddMinutes(15)), Configuration, "tibber").EnergyKwh);
        var held = Log(charged, null);
        Assert.Equal(.096m, GridChargeEnergyCredit.Update(held, new BatteryState(15.5m, Start.AddMinutes(30)), Configuration, "tibber").EnergyKwh);
        Assert.Equal(0m, GridChargeEnergyCredit.Update(held, new BatteryState(15m, Start.AddMinutes(30)), Configuration, "tibber").EnergyKwh);
        Assert.Equal(.192m, GridChargeEnergyCredit.Update(held, new BatteryState(17m, Start.AddMinutes(30)), Configuration, "tibber").EnergyKwh);
    }

    [Fact]
    public void CreditDeductsPvSurplusButNotPvServingHouseDemand()
    {
        var credit = GridChargeEnergyCredit.Update(null, new BatteryState(15m, Start), Configuration, "tibber");
        var measured = new BatteryState(16m, Start.AddMinutes(15));
        Assert.Equal(.192m, GridChargeEnergyCredit.Update(Log(credit, BatteryChargeSource.Grid, pv: 2000),
            measured, Configuration, "tibber").EnergyKwh); // 800W net demand: no PV surplus
        Assert.Equal(0m, GridChargeEnergyCredit.Update(Log(credit, BatteryChargeSource.Grid, pv: 2000, grid: -2000),
            measured, Configuration, "tibber").EnergyKwh);
    }

    [Theory]
    [InlineData(1500, false)]
    [InlineData(3000, false)]
    [InlineData(1500, true)]
    public void RepeatedDecisionsRetainEachCommandUntilTheNextSocMeasurement(int nextPower, bool manual)
    {
        var unchanged = new BatteryState(15m, Start);
        var credit = GridChargeEnergyCredit.Update(null, unchanged, Configuration, "tibber");
        var first = Log(credit, BatteryChargeSource.Grid);
        var pending = GridChargeEnergyCredit.Update(first, unchanged, Configuration, "tibber");
        Assert.Equal(0m, pending.EnergyKwh); // Commands alone never earn credit.
        var second = Log(pending, BatteryChargeSource.Grid, manual: manual,
            decidedAt: Start.AddSeconds(45), targetPower: nextPower);
        // Simulate persistence/restart between decisions, including the unmeasured command intervals.
        var repeated = GridChargeEnergyCredit.Update(second, unchanged, Configuration, "tibber");
        var third = Log(repeated, BatteryChargeSource.Grid, manual: manual,
            decidedAt: Start.AddSeconds(90), targetPower: nextPower);
        var charged = GridChargeEnergyCredit.Update(third,
            new BatteryState(16m, Start.AddMinutes(2)), Configuration, "tibber");
        var efficiency = (decimal)Math.Sqrt(.9);
        var expected = Math.Min(.192m, (1500m * 45m + (manual ? 0m : nextPower * 75m)) / 3600000m * efficiency);
        Assert.InRange(charged.EnergyKwh, expected - .000001m, expected + .000001m);
        // Re-reading the same measured sample cannot count the intervals twice.
        Assert.Equal(charged.EnergyKwh, GridChargeEnergyCredit.Update(
            Log(charged, null, decidedAt: Start.AddMinutes(2)),
            new BatteryState(16m, Start.AddMinutes(2)), Configuration, "tibber").EnergyKwh);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(BatteryChargeSource.PV)]
    public void NonGridCommandsDoNotEraseEarlierMeasuredPurchaseOrExtendItsDuration(BatteryChargeSource? source)
    {
        var unchanged = new BatteryState(15m, Start);
        var credit = GridChargeEnergyCredit.Update(null, unchanged, Configuration, "tibber");
        var pending = GridChargeEnergyCredit.Update(Log(credit, BatteryChargeSource.Grid), unchanged, Configuration, "tibber");
        var switched = Log(pending, source, decidedAt: Start.AddMinutes(1));
        var measured = new BatteryState(16m, Start.AddMinutes(2));
        var charged = GridChargeEnergyCredit.Update(switched, measured, Configuration, "tibber");
        var expected = 1.5m / 60m * (decimal)Math.Sqrt(.9);
        Assert.InRange(charged.EnergyKwh, expected - .000001m, expected + .000001m);
        Assert.Empty(charged.PendingCommands);
        Assert.Equal(0m, GridChargeEnergyCredit.Update(switched, measured, Configuration, "other-supplier").EnergyKwh);
    }

    private static DecisionLogEntry Log(GridChargeEnergyCredit credit, BatteryChargeSource? source,
        bool manual = false, int pv = 0, int grid = 800, DateTimeOffset? decidedAt = null, int targetPower = 1500) => new(
        Guid.NewGuid(), decidedAt ?? credit.MeasuredAtUtc, decidedAt ?? credit.MeasuredAtUtc, (decidedAt ?? credit.MeasuredAtUtc).AddMinutes(15),
        new CurrentBatteryDecision(new BatteryDecisionInstruction(source is null ? BatteryDecisionState.Idle : BatteryDecisionState.Charge, source), source is null ? 0 : targetPower),
        credit.StateOfChargePercent, .3m, "EUR", grid, 0,
        JsonSerializer.Serialize(new { GridChargeEnergyCredit = credit, CurrentPvProductionWatts = pv,
            CurrentGridImportWatts = grid, CurrentBatteryPowerWatts = 0 }),
        [new BatteryDecisionReason(manual ? CurrentBatteryDecisionRuleIds.ManualGridCharge : BatteryForecastRuleIds.PlannedGridCharge, "Test.")]);

    private sealed record Clock(DateTimeOffset UtcNow) : IUtcClock;
    private sealed record Telemetry(CurrentSiteTelemetry Value) : ICurrentSiteTelemetryProvider
    {
        public Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }

    private sealed class History : IDecisionLogRepository
    {
        public List<DecisionLogEntry> Entries { get; } = [];
        public Task SaveDecisionAsync(DecisionLogEntry entry, CancellationToken cancellationToken = default)
        { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<DecisionLogEntry>> GetRecentDecisionsAsync(int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DecisionLogEntry>>(Entries.OrderByDescending(e => e.DecidedAtUtc).Take(maxCount).ToArray());
        public Task<IReadOnlyList<DecisionLogEntry>> GetDecisionsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DecisionLogEntry>>([]);
        public Task<int> DeleteDecisionsOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class ForecastInputs : IElectricityPriceForecastProvider, IWeatherForecastProvider, IHistoricalConsumptionProvider
    {
        public static readonly decimal[] Prices = [.3762m, .44m, .4654m];
        public static readonly decimal[] Loads = [.2m, .4m, .354m];
        public string Source => "tibber";
        private static IEnumerable<(ForecastTimeSlot Slot, int Index)> Slots(DateTimeOffset from, DateTimeOffset to) =>
            Enumerable.Range(0, 3).Select(i => (Slot: new ForecastTimeSlot(Start.AddMinutes(15 * i), Start.AddMinutes(15 * (i + 1))), Index: i))
                .Where(item => item.Slot.StartsAtUtc >= from && item.Slot.EndsAtUtc <= to);
        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TibberPriceForecastSlot>>(Slots(startsAtUtc, endsAtUtc).Select(s => new TibberPriceForecastSlot(s.Slot, Prices[s.Index], "EUR")).ToArray());
        public Task<IReadOnlyList<PvYieldForecastSlot>> GetPvYieldForecastAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PvYieldForecastSlot>>(Slots(startsAtUtc, endsAtUtc).Select(s => new PvYieldForecastSlot(s.Slot, 0m)).ToArray());
        public Task<IReadOnlyList<ConsumptionForecastSlot>> GetConsumptionForecastAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConsumptionForecastSlot>>(Slots(startsAtUtc, endsAtUtc).Select(s => new ConsumptionForecastSlot(s.Slot, Loads[s.Index])).ToArray());
    }
}
