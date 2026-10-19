using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfWeishauptBiblockTelemetryRepository : IWeishauptBiblockTelemetryRepository
{
    private readonly ControllerDbContext dbContext;

    public EfWeishauptBiblockTelemetryRepository(ControllerDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task SaveSampleAsync(WeishauptBiblockTelemetrySample sample, CancellationToken cancellationToken = default)
    {
        dbContext.WeishauptBiblockTelemetrySamples.Add(new WeishauptBiblockTelemetrySampleEntity
        {
            MeasuredAtUtc = sample.MeasuredAtUtc,
            OutdoorTemperatureCelsius = sample.OutdoorTemperatureCelsius,
            OperatingStatusCode = sample.OperatingStatusCode,
            HotWaterTemperatureCelsius = sample.HotWaterTemperatureCelsius
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default)
    {
        if (thresholdUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Bereinigungszeitpunkt der Weishaupt-Telemetrie muss in UTC angegeben sein.", nameof(thresholdUtc));
        }

        return await dbContext.WeishauptBiblockTelemetrySamples
            .Where(sample => sample.MeasuredAtUtc < thresholdUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WeishauptBiblockTelemetrySample>> GetSamplesAsync(
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero || endsAtUtc.Offset != TimeSpan.Zero || startsAtUtc >= endsAtUtc)
            throw new ArgumentException("Der Weishaupt-Abfragezeitraum muss in UTC vorwaerts verlaufen.");
        return await dbContext.WeishauptBiblockTelemetrySamples.AsNoTracking()
            .Where(s => s.MeasuredAtUtc >= startsAtUtc && s.MeasuredAtUtc < endsAtUtc).OrderBy(s => s.MeasuredAtUtc)
            .Select(s => new WeishauptBiblockTelemetrySample
            {
                MeasuredAtUtc = s.MeasuredAtUtc, HotWaterTemperatureCelsius = s.HotWaterTemperatureCelsius,
                OutdoorTemperatureCelsius = s.OutdoorTemperatureCelsius, OperatingStatusCode = s.OperatingStatusCode
            }).ToArrayAsync(cancellationToken);
    }
}
