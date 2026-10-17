using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides hourly outdoor temperatures for the site, past and future, so the consumption forecast can follow heating demand.
/// </summary>
public interface ITemperatureProvider
{
    Task<IReadOnlyList<HourlyTemperature>> GetHourlyTemperaturesAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);
}
