using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfBatterySavingsRepository : IBatterySavingsRepository
{
    private readonly ControllerDbContext dbContext;

    public EfBatterySavingsRepository(ControllerDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task SaveDailySummaryAsync(
        BatterySavingsDailySummary summary,
        CancellationToken cancellationToken = default)
    {
        if (summary is null)
        {
            throw new ArgumentNullException(nameof(summary), "Der Batterie-Ersparnis-Tageswert darf nicht null sein.");
        }

        var existingSummary = await dbContext.BatterySavingsDailySummaries
            .SingleOrDefaultAsync(entity =>
                entity.AccountingDate == summary.AccountingDate &&
                entity.Currency == summary.Currency,
                cancellationToken);

        if (existingSummary is null)
        {
            dbContext.BatterySavingsDailySummaries.Add(MapToEntity(summary));
        }
        else
        {
            UpdateEntity(existingSummary, summary);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BatterySavingsDailySummary>> GetDailySummariesAsync(
        BatterySavingsQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(query);

        return await dbContext.BatterySavingsDailySummaries
            .Where(summary =>
                summary.Currency == query.Currency &&
                summary.AccountingDate >= query.StartDate &&
                summary.AccountingDate <= query.EndDate)
            .OrderBy(summary => summary.AccountingDate)
            .Select(summary => MapToDomain(summary))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<BatterySavingsAggregate> GetAggregateAsync(
        BatterySavingsQuery query,
        CancellationToken cancellationToken = default)
    {
        var summaries = await GetDailySummariesAsync(query, cancellationToken);

        return BatterySavingsAggregate.FromDailySummaries(summaries);
    }

    private static void ValidateQuery(BatterySavingsQuery query)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query), "Die Batterie-Ersparnis-Abfrage darf nicht null sein.");
        }

        if (query.EndDate < query.StartDate)
        {
            throw new ArgumentException("Das Enddatum der Batterie-Ersparnis-Abfrage muss nach dem Startdatum liegen.", nameof(query));
        }

        if (string.IsNullOrWhiteSpace(query.Currency))
        {
            throw new ArgumentException("Die Waehrung der Batterie-Ersparnis-Abfrage muss angegeben werden.", nameof(query));
        }
    }

    private static BatterySavingsDailySummaryEntity MapToEntity(BatterySavingsDailySummary summary)
    {
        var entity = new BatterySavingsDailySummaryEntity
        {
            AccountingDate = summary.AccountingDate,
            Currency = summary.Currency
        };

        UpdateEntity(entity, summary);

        return entity;
    }

    private static void UpdateEntity(
        BatterySavingsDailySummaryEntity entity,
        BatterySavingsDailySummary summary)
    {
        entity.GridChargedEnergyKwh = summary.GridChargedEnergyKwh;
        entity.GridChargeCost = summary.GridChargeCost;
        entity.PvChargedEnergyKwh = summary.PvChargedEnergyKwh;
        entity.PvOpportunityCost = summary.PvOpportunityCost;
        entity.DischargedEnergyKwh = summary.DischargedEnergyKwh;
        entity.DischargeAvoidedCost = summary.DischargeAvoidedCost;
        entity.NetSavings = summary.NetSavings;
        entity.UpdatedAtUtc = summary.UpdatedAtUtc;
    }

    private static BatterySavingsDailySummary MapToDomain(BatterySavingsDailySummaryEntity entity)
    {
        var values = new BatterySavingsDailySummaryValues
        {
            AccountingDate = entity.AccountingDate,
            Currency = entity.Currency,
            GridChargedEnergyKwh = entity.GridChargedEnergyKwh,
            GridChargeCost = entity.GridChargeCost,
            PvChargedEnergyKwh = entity.PvChargedEnergyKwh,
            PvOpportunityCost = entity.PvOpportunityCost,
            DischargedEnergyKwh = entity.DischargedEnergyKwh,
            DischargeAvoidedCost = entity.DischargeAvoidedCost,
            NetSavings = entity.NetSavings,
            UpdatedAtUtc = entity.UpdatedAtUtc
        };

        return new BatterySavingsDailySummary(values);
    }
}
