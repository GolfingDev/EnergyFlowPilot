using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Energy;

public sealed record EnergyCostReportDependencies(IEnergyQuarterHourRepository QuarterHours, IControllerSettingStore Settings, IUtcClock Clock);

/// <summary>Electricity cost and consumption report, day detail and tariff periods for the page "Stromkosten &amp; Verbrauch".</summary>
public sealed class EnergyCostReportService(EnergyCostReportDependencies dependencies)
{
    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken = default) =>
        TimeZoneInfo.FindSystemTimeZoneById((await dependencies.Settings.GetSettingAsync(ControllerSettingDefaults.ConsumptionForecastTimeZoneKey,
            cancellationToken))?.Value ?? "Europe/Berlin");

    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken = default) =>
        EnergyPeriods.LocalDay(dependencies.Clock.UtcNow, await ZoneAsync(cancellationToken));

    public async Task<EnergyCostReport> GetReportAsync(EnergyPeriod period, CancellationToken cancellationToken = default)
    {
        var zone = await ZoneAsync(cancellationToken);
        var (previousFrom, _) = EnergyPeriods.ToUtc(EnergyPeriods.Shift(period, -1), zone);
        var (_, toUtc) = EnergyPeriods.ToUtc(period, zone);
        var quarters = await dependencies.QuarterHours.GetAsync(previousFrom, toUtc, cancellationToken);
        return EnergyCostReportBuilder.Build(new EnergyCostReportInput(period, dependencies.Clock.UtcNow, zone, quarters,
            await GetTariffsAsync(cancellationToken))
        {
            FirstBalanceMeasurementUtc = await dependencies.QuarterHours.GetFirstBalanceSlotAsync(cancellationToken)
        });
    }

    /// <summary>The measured quarter hours of one local day (92 to 100 of them on daylight saving days).</summary>
    public async Task<IReadOnlyList<EnergyQuarterHour>> GetDayAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var zone = await ZoneAsync(cancellationToken);
        return await dependencies.QuarterHours.GetAsync(EnergyPeriods.StartOfDayUtc(day, zone), EnergyPeriods.StartOfDayUtc(day.AddDays(1), zone),
            cancellationToken);
    }

    public async Task<IReadOnlyList<TariffPeriod>> GetTariffsAsync(CancellationToken cancellationToken = default) =>
        TariffPeriods.Parse((await dependencies.Settings.GetSettingAsync(ControllerSettingDefaults.ElectricityTariffPeriodsKey, cancellationToken))?.Value);

    public async Task<IReadOnlyList<TariffPeriod>> SaveTariffsAsync(IEnumerable<TariffPeriod> periods, CancellationToken cancellationToken = default)
    {
        var validated = TariffPeriods.Validate(periods);
        await dependencies.Settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.ElectricityTariffPeriodsKey,
            TariffPeriods.Serialize(validated), ControllerSettingSensitivity.Normal, dependencies.Clock.UtcNow), cancellationToken);
        return validated;
    }
}
