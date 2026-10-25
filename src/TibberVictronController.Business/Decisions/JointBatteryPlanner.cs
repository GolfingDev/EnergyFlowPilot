using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Two-dimensional cost planning with separate physical energy states and interpolated future costs.</summary>
public sealed class JointBatteryPlanner
{
    private const double Eps = 1e-8;
    private sealed record Unit(JointBatteryUnit Source)
    {
        internal double Capacity => (double)Source.Configuration.TotalCapacityKwh;
        internal double Reserve => Capacity * (double)Source.Configuration.PlanningMinimumStateOfChargePercent / 100;
        internal double GridMaximum => Capacity * (double)Source.Configuration.PlanningMaximumStateOfChargePercent / 100;
        internal double Eta => Math.Sqrt((double)Source.Configuration.RoundTripEfficiencyPercent / 100);
        internal double Charge => Math.Min(Source.Configuration.MaximumChargePowerWatts, Source.AvailableChargeWatts ?? int.MaxValue) / 1000d;
        internal double Discharge => Math.Min(Source.Configuration.MaximumDischargePowerWatts, Source.AvailableDischargeWatts ?? int.MaxValue) / 1000d;
        internal double Initial => Capacity * (double)Source.State.StateOfChargePercent / 100;
        internal double Delta(double power) => power * .25 * (power >= 0 ? Eta : 1 / Eta);
        internal double Power(double delta) => delta / .25 * (delta >= 0 ? 1 / Eta : Eta);
        internal double[] Grid() => Enumerable.Range(0, 41).Select(i => Capacity * i / 40)
            .Concat([Initial, Reserve, GridMaximum]).Distinct().Order().ToArray();
    }
    private readonly record struct Action(double A, double B, double Cost);

    public JointBatteryPlan Calculate(JointBatteryPlanInput input, CancellationToken token = default)
    {
        if (input.Batteries.Count != 2 || input.Batteries.Select(b => b.Id).Distinct().Count() != 2)
            throw new ArgumentException("Die gemeinsame Planung benoetigt zwei getrennte Speicher.");
        if (input.NowUtc.Offset != TimeSpan.Zero || input.Batteries.Any(b => input.NowUtc - b.State.MeasuredAtUtc > TimeSpan.FromSeconds(30) || b.State.MeasuredAtUtc > input.NowUtc.AddSeconds(5)))
            throw new InvalidOperationException("Die gemeinsame Planung benoetigt aktuelle Messwerte beider Speicher.");
        if (input.MaximumGridImportWatts < 0 || input.Prices.Count is 0 or > 288)
            throw new InvalidOperationException("Netzbudget oder Planungshorizont ist ungueltig.");
        var prices = input.Prices.OrderBy(p => p.TimeSlot.StartsAtUtc).ToArray();
        if (prices.Any(p => !p.TimeSlot.IsFifteenMinuteSlot || p.Currency != prices[0].Currency) || prices.Zip(prices.Skip(1)).Any(p => p.First.TimeSlot.EndsAtUtc != p.Second.TimeSlot.StartsAtUtc))
            throw new InvalidOperationException("Die gemeinsame Planung benoetigt lueckenlose Viertelstundenpreise in einer Waehrung.");
        var pv = input.Pv.ToDictionary(s => s.TimeSlot, s => s.ExpectedPvYieldKwh);
        var load = input.Consumption.ToDictionary(s => s.TimeSlot, s => s.ExpectedConsumptionKwh);
        if (prices.Any(p => !pv.ContainsKey(p.TimeSlot) || !load.ContainsKey(p.TimeSlot)))
            throw new InvalidOperationException("PV- und Verbrauchsdaten muessen alle Preiszeitabschnitte abdecken.");
        var a = new Unit(input.Batteries[0]); var b = new Unit(input.Batteries[1]);
        var ga = a.Grid(); var gb = b.Grid();
        var costs = new double[prices.Length + 1][];
        costs[^1] = new double[ga.Length * gb.Length];
        var continuation = input.Continuation;
        if (continuation is { ValuePerDeliveredKwh: < 0 } or { MaximumDeliveredEnergyKwh: < 0 })
            throw new ArgumentException("Der Restenergiewert und der Folgebedarf duerfen nicht negativ sein.");
        if (continuation is not null)
        {
            // Reward energy only once, at the horizon boundary, after discharge losses and only up to shared future demand.
            // Purchase hurdles/wear remain in the normal transitions; there is no separate cheap-price fill rule.
            for (var i = 0; i < ga.Length; i++)
            for (var j = 0; j < gb.Length; j++)
            {
                var deliverable = Math.Max(0, ga[i] - a.Reserve) * a.Eta + Math.Max(0, gb[j] - b.Reserve) * b.Eta;
                costs[^1][i * gb.Length + j] = -Math.Min(deliverable, (double)continuation.MaximumDeliveredEnergyKwh) * (double)continuation.ValuePerDeliveredKwh;
            }
        }
        var net = prices.Select(p => (double)(load[p.TimeSlot] - pv[p.TimeSlot])).ToArray();
        var budget = input.MaximumGridImportWatts == 0 ? double.PositiveInfinity : input.MaximumGridImportWatts / 1000d;
        var penaltyPerKwh = (double)input.Batteries.Max(u => u.Configuration.MinimumSavingsPerKwh + u.Configuration.WearCostPerKwh);

        Action Choose(int slot, double energyA, double energyB)
        {
            var actionsA = Candidates(a, ga, energyA); var actionsB = Candidates(b, gb, energyB);
            var best = new Action(0, 0, double.PositiveInfinity);
            void Consider(double pa, double pb)
            {
                if (pa * pb < -Eps || pa > a.Charge + Eps || pb > b.Charge + Eps || pa < -a.Discharge - Eps || pb < -b.Discharge - Eps) return;
                var ea = energyA + a.Delta(pa); var eb = energyB + b.Delta(pb);
                if (ea < Math.Min(energyA, a.Reserve) - Eps || eb < Math.Min(energyB, b.Reserve) - Eps || ea > a.Capacity + Eps || eb > b.Capacity + Eps) return;
                var power = pa + pb; var grid = net[slot] + power * .25;
                if (power < -Eps && grid < -Eps) return;
                var gridCharge = power > Eps && grid > Eps;
                if (gridCharge && (prices[slot].IsEstimated || (pa > Eps && ea > a.GridMaximum + Eps) || (pb > Eps && eb > b.GridMaximum + Eps))) return;
                if (power > Eps && grid / .25 > budget + Eps) return;
                var penalty = gridCharge ? Math.Min(power * .25, Math.Max(0, grid)) * penaltyPerKwh : 0;
                var cost = grid * (grid >= 0 ? (double)prices[slot].TotalPricePerKwh : (double)input.FeedInCompensationPerKwh) + penalty +
                    Interpolate(costs[slot + 1], ga, gb, ea, eb) + (Math.Abs(pa) + Math.Abs(pb)) * 1e-9;
                if (cost < best.Cost - 1e-10) best = new(pa, pb, cost);
            }
            foreach (var pa in actionsA)
            {
                foreach (var pb in actionsB) Consider(pa, pb);
                Consider(pa, -net[slot] / .25 - pa);
                if (!double.IsInfinity(budget)) Consider(pa, budget - net[slot] / .25 - pa);
            }
            foreach (var pb in actionsB)
            {
                Consider(-net[slot] / .25 - pb, pb);
                if (!double.IsInfinity(budget)) Consider(budget - net[slot] / .25 - pb, pb);
            }
            return best;
        }

        // Backward dynamic programming retains both energy dimensions; no shared SoC masks an empty battery.
        for (var slot = prices.Length - 1; slot >= 0; slot--)
        {
            costs[slot] = new double[ga.Length * gb.Length];
            for (var i = 0; i < ga.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                for (var j = 0; j < gb.Length; j++) costs[slot][i * gb.Length + j] = Choose(slot, ga[i], gb[j]).Cost;
            }
        }
        var currentA = a.Initial; var currentB = b.Initial;
        var slots = new List<JointBatterySlot>();
        for (var i = 0; i < prices.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var action = Choose(i, currentA, currentB);
            var wa = (int)Math.CopySign(Math.Floor(Math.Abs(action.A * 1000) + 1e-7), action.A);
            var wb = (int)Math.CopySign(Math.Floor(Math.Abs(action.B * 1000) + 1e-7), action.B);
            var afterA = currentA + a.Delta(wa / 1000d); var afterB = currentB + b.Delta(wb / 1000d);
            var targets = new JointBatteryTarget[] {
                new(a.Source.Id, wa, (decimal)(currentA / a.Capacity * 100), (decimal)(afterA / a.Capacity * 100)),
                new(b.Source.Id, wb, (decimal)(currentB / b.Capacity * 100), (decimal)(afterB / b.Capacity * 100)) };
            var total = wa + wb;
            BatteryChargeSource? source = total <= 0 ? null : net[i] + total / 1000d * .25 > Eps ? BatteryChargeSource.Grid : BatteryChargeSource.PV;
            slots.Add(new(prices[i].TimeSlot, prices[i].TotalPricePerKwh, prices[i].Currency, prices[i].IsEstimated, source, targets, string.Empty, false,
                pv[prices[i].TimeSlot], load[prices[i].TimeSlot]));
            currentA = afterA; currentB = afterB;
        }
        var plan = new JointBatteryPlan(input.NowUtc, input.Batteries, slots, input.MaximumGridImportWatts,
            ["Numerische Planung mit zwei Energiezustaenden und interpolierten Folgekosten; keine analytisch exakte Optimierung.",
             continuation is null ? "Keine belastbare Preis- oder Folgeprognose; keine zusaetzliche Bevorratung ueber den Preishorizont hinaus."
                : FormattableString.Invariant($"Restenergiewert: {continuation.HistoricalDays} historische Tage, typischer Preis {continuation.HistoricalTypicalPricePerKwh:F4} EUR/kWh; nach 15 % Unsicherheitsabschlag {continuation.ValuePerDeliveredKwh:F4} EUR je spaeter abgegebener kWh, begrenzt auf {continuation.MaximumDeliveredEnergyKwh:F2} kWh prognostizierten Netto-Folgebedarf.")]);
        return JointBatteryPlanExplainer.Explain(plan, continuation);
    }

    private static double[] Candidates(Unit unit, double[] grid, double energy)
    {
        var min = Math.Max(Math.Min(energy, unit.Reserve), energy + unit.Delta(-unit.Discharge));
        var max = Math.Min(unit.Capacity, energy + unit.Delta(unit.Charge));
        return grid.Where(e => e >= min - Eps && e <= max + Eps).Concat([energy, min, max, Math.Clamp(unit.GridMaximum, min, max)])
            .Select(e => unit.Power(e - energy)).Distinct().ToArray();
    }
    private static double Interpolate(double[] values, double[] a, double[] b, double ea, double eb)
    {
        static (int Index, double Weight) Position(double[] grid, double value)
        {
            var index = Array.BinarySearch(grid, value);
            if (index < 0) index = ~index - 1;
            index = Math.Clamp(index, 0, grid.Length - 2);
            return (index, Math.Clamp((value - grid[index]) / (grid[index + 1] - grid[index]), 0, 1));
        }
        var (i, x) = Position(a, ea); var (j, y) = Position(b, eb);
        return (1 - x) * ((1 - y) * values[i * b.Length + j] + y * values[i * b.Length + j + 1]) +
            x * ((1 - y) * values[(i + 1) * b.Length + j] + y * values[(i + 1) * b.Length + j + 1]);
    }
}
