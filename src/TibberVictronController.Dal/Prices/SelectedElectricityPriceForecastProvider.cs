using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Octopus;
using TibberVictronController.Dal.Tibber;

namespace TibberVictronController.Dal.Prices;

/// <summary>One scoped selection shared by all consumers. Unknown providers never silently use Tibber.</summary>
public sealed class SelectedElectricityPriceForecastProvider(
    IControllerSettingStore settings, TibberPriceForecastProvider tibber, OctopusPriceForecastProvider octopus)
    : IElectricityPriceForecastProvider
{
    private string? selectedSource;
    public string Source => selectedSource ?? "tibber";

    public async Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default)
    {
        selectedSource ??= (await settings.GetSettingAsync(ControllerSettingDefaults.ElectricityPriceProviderKey, cancellationToken))?.Value ?? "tibber";
        return Source switch
        {
            "tibber" => await tibber.GetPriceForecastAsync(startsAtUtc, endsAtUtc, cancellationToken),
            "octopusGermany" => await octopus.GetPriceForecastAsync(startsAtUtc, endsAtUtc, cancellationToken),
            _ => throw new InvalidOperationException("Unbekannte Strompreisquelle; tibber oder octopusGermany auswaehlen.")
        };
    }
}
