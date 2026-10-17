using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfHomematicClimateRepository : IHomematicClimateRepository
{
    private readonly ControllerDbContext dbContext;

    public EfHomematicClimateRepository(ControllerDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task SaveSamplesAsync(IReadOnlyList<HomematicClimateSample> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0)
        {
            return;
        }

        dbContext.HomematicClimateSamples.AddRange(samples.Select(sample => new HomematicClimateSampleEntity
        {
            PolledAtUtc = sample.PolledAtUtc,
            LastChangedAtUtc = sample.LastChangedAtUtc,
            ChannelAddress = sample.ChannelAddress,
            DeviceType = sample.DeviceType,
            DeviceName = sample.DeviceName,
            ChannelName = sample.ChannelName,
            Kind = sample.Kind,
            ActualTemperatureCelsius = sample.ActualTemperatureCelsius,
            SetPointTemperatureCelsius = sample.SetPointTemperatureCelsius,
            SetPointMode = sample.SetPointMode,
            ValveOpeningPercent = sample.ValveOpeningPercent,
            RelativeHumidityPercent = sample.RelativeHumidityPercent,
            IsWindowOpen = sample.IsWindowOpen,
            IsReachable = sample.IsReachable
        }));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HomematicClimateSample>> GetSamplesAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Startzeitpunkt fuer Homematic-Klimadaten muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        if (endsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Endzeitpunkt fuer Homematic-Klimadaten muss in UTC angegeben sein.", nameof(endsAtUtc));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Der Endzeitpunkt fuer Homematic-Klimadaten muss nach dem Startzeitpunkt liegen.", nameof(endsAtUtc));
        }

        return await dbContext.HomematicClimateSamples
            .AsNoTracking()
            .Where(sample => sample.PolledAtUtc >= startsAtUtc && sample.PolledAtUtc < endsAtUtc)
            .OrderBy(sample => sample.PolledAtUtc)
            .ThenBy(sample => sample.ChannelAddress)
            .Select(sample => new HomematicClimateSample
            {
                PolledAtUtc = sample.PolledAtUtc,
                LastChangedAtUtc = sample.LastChangedAtUtc,
                ChannelAddress = sample.ChannelAddress,
                DeviceType = sample.DeviceType,
                DeviceName = sample.DeviceName,
                ChannelName = sample.ChannelName,
                Kind = sample.Kind,
                ActualTemperatureCelsius = sample.ActualTemperatureCelsius,
                SetPointTemperatureCelsius = sample.SetPointTemperatureCelsius,
                SetPointMode = sample.SetPointMode,
                ValveOpeningPercent = sample.ValveOpeningPercent,
                RelativeHumidityPercent = sample.RelativeHumidityPercent,
                IsWindowOpen = sample.IsWindowOpen,
                IsReachable = sample.IsReachable
            })
            .ToArrayAsync(cancellationToken);
    }

    public async Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default)
    {
        if (thresholdUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Bereinigungszeitpunkt fuer Homematic-Klimadaten muss in UTC angegeben sein.", nameof(thresholdUtc));
        }

        // Set-based delete: the table grows to about a million rows, loading them first would be wasteful on the Pi.
        return await dbContext.HomematicClimateSamples
            .Where(sample => sample.PolledAtUtc < thresholdUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
