using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Energy;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

/// <summary>
/// Reads only the few columns the energy history needs (never the decision log's input JSON); both tables have an
/// index on their time column.
/// </summary>
public sealed class EfEnergyMeasurementReader(ControllerDbContext dbContext) : IEnergyMeasurementReader
{
    public async Task<IReadOnlyList<EnergyPowerPoint>> GetDecisionLogPointsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.DecisionLogEntries.AsNoTracking()
            .Where(entry => entry.DecidedAtUtc >= fromUtc && entry.DecidedAtUtc < toUtc)
            .OrderBy(entry => entry.DecidedAtUtc)
            .Select(entry => new { entry.DecidedAtUtc, entry.GridImportWatts, entry.GridExportWatts, entry.TibberPricePerKwh, entry.TibberPriceCurrency })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new EnergyPowerPoint(row.DecidedAtUtc)
        {
            // Import and export are logged as two non-negative values; a missing one means the grid was not measured.
            GridWatts = row is { GridImportWatts: { } import, GridExportWatts: { } export } ? import - export : null,
            PricePerKwh = row.TibberPriceCurrency == "EUR" ? row.TibberPricePerKwh : null
        }).ToArray();
    }

    public async Task<IReadOnlyList<EnergyPowerPoint>> GetLivePointsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.LiveConsumptionSamples.AsNoTracking()
            .Where(sample => sample.MeasuredAtUtc >= fromUtc && sample.MeasuredAtUtc < toUtc)
            .OrderBy(sample => sample.MeasuredAtUtc)
            .Select(sample => new
            {
                sample.MeasuredAtUtc, sample.GridPowerWatts, sample.BatteryPowerWatts, sample.SupplementaryBatteryPowerWatts,
                sample.SupplementaryPvProductionWatts, sample.EvChargingWatts
            })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new EnergyPowerPoint(row.MeasuredAtUtc)
        {
            GridWatts = (double?)row.GridPowerWatts,
            PrimaryBatteryWatts = (double?)row.BatteryPowerWatts,
            SupplementaryBatteryWatts = (double?)row.SupplementaryBatteryPowerWatts,
            SupplementaryPvWatts = (double?)row.SupplementaryPvProductionWatts,
            EvWatts = (double?)row.EvChargingWatts
        }).ToArray();
    }

    public async Task<DateTimeOffset?> GetFirstMeasurementAtUtcAsync(CancellationToken cancellationToken = default)
    {
        var first = await dbContext.DecisionLogEntries.AsNoTracking().OrderBy(entry => entry.DecidedAtUtc)
            .Select(entry => (DateTimeOffset?)entry.DecidedAtUtc).FirstOrDefaultAsync(cancellationToken);
        return first;
    }
}

public sealed class EfEnergyQuarterHourRepository(ControllerDbContext dbContext) : IEnergyQuarterHourRepository
{
    public async Task<IReadOnlyList<EnergyQuarterHour>> GetAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.EnergyQuarterHours.AsNoTracking()
            .Where(entity => entity.SlotStartUtc >= fromUtc && entity.SlotStartUtc < toUtc)
            .OrderBy(entity => entity.SlotStartUtc)
            .ToListAsync(cancellationToken);
        return entities.Select(ToModel).ToArray();
    }

    public async Task<DateTimeOffset?> GetFirstBalanceSlotAsync(CancellationToken cancellationToken = default) =>
        await dbContext.EnergyQuarterHours.AsNoTracking().Where(entity => entity.BalanceCoveredSeconds > 0).OrderBy(entity => entity.SlotStartUtc)
            .Select(entity => (DateTimeOffset?)entity.SlotStartUtc).FirstOrDefaultAsync(cancellationToken);

    public async Task SaveAsync(IReadOnlyCollection<EnergyQuarterHour> quarterHours, CancellationToken cancellationToken = default)
    {
        if (quarterHours.Count == 0) return;
        var from = quarterHours.Min(slot => slot.SlotStartUtc);
        var to = quarterHours.Max(slot => slot.SlotStartUtc);
        var existing = await dbContext.EnergyQuarterHours.Where(entity => entity.SlotStartUtc >= from && entity.SlotStartUtc <= to)
            .ToDictionaryAsync(entity => entity.SlotStartUtc, cancellationToken);
        foreach (var slot in quarterHours)
        {
            if (!existing.TryGetValue(slot.SlotStartUtc, out var entity))
            {
                entity = new EnergyQuarterHourEntity { SlotStartUtc = slot.SlotStartUtc };
                dbContext.EnergyQuarterHours.Add(entity);
                existing[slot.SlotStartUtc] = entity;
            }

            entity.GridSource = slot.GridSource.ToString();
            entity.GridCoveredSeconds = slot.GridCoveredSeconds;
            entity.GridImportKwh = slot.GridImportKwh;
            entity.GridExportKwh = slot.GridExportKwh;
            entity.BalanceCoveredSeconds = slot.BalanceCoveredSeconds;
            entity.HouseConsumptionKwh = slot.HouseConsumptionKwh;
            entity.PvProductionKwh = slot.PvProductionKwh;
            entity.PrimaryBatteryChargeKwh = slot.PrimaryBatteryChargeKwh;
            entity.PrimaryBatteryDischargeKwh = slot.PrimaryBatteryDischargeKwh;
            entity.SupplementaryBatteryChargeKwh = slot.SupplementaryBatteryChargeKwh;
            entity.SupplementaryBatteryDischargeKwh = slot.SupplementaryBatteryDischargeKwh;
            entity.EvCoveredSeconds = slot.EvCoveredSeconds;
            entity.EvChargingKwh = slot.EvChargingKwh;
            entity.PriceCoveredSeconds = slot.PriceCoveredSeconds;
            entity.PricePerKwh = slot.PricePerKwh;
            entity.ComputedAtUtc = slot.ComputedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static EnergyQuarterHour ToModel(EnergyQuarterHourEntity entity) => new(entity.SlotStartUtc)
    {
        GridSource = Enum.TryParse<EnergySampleSource>(entity.GridSource, out var source) ? source : EnergySampleSource.DecisionLog,
        GridCoveredSeconds = entity.GridCoveredSeconds,
        GridImportKwh = entity.GridImportKwh,
        GridExportKwh = entity.GridExportKwh,
        BalanceCoveredSeconds = entity.BalanceCoveredSeconds,
        HouseConsumptionKwh = entity.HouseConsumptionKwh,
        PvProductionKwh = entity.PvProductionKwh,
        PrimaryBatteryChargeKwh = entity.PrimaryBatteryChargeKwh,
        PrimaryBatteryDischargeKwh = entity.PrimaryBatteryDischargeKwh,
        SupplementaryBatteryChargeKwh = entity.SupplementaryBatteryChargeKwh,
        SupplementaryBatteryDischargeKwh = entity.SupplementaryBatteryDischargeKwh,
        EvCoveredSeconds = entity.EvCoveredSeconds,
        EvChargingKwh = entity.EvChargingKwh,
        PriceCoveredSeconds = entity.PriceCoveredSeconds,
        PricePerKwh = entity.PricePerKwh,
        ComputedAtUtc = entity.ComputedAtUtc
    };
}
