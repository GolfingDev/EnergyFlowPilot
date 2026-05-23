using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.HagerEnergy;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Dal.Battery;

/// <summary>
/// Routes live telemetry reads to the currently configured energy device integration.
/// </summary>
public sealed class SelectedCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly TelemetrySourceSelector sourceSelector;
    private readonly ResilientCurrentSiteTelemetryProvider victronCurrentSiteTelemetryProvider;
    private readonly HagerEnergyCurrentSiteTelemetryProvider hagerEnergyCurrentSiteTelemetryProvider;

    public SelectedCurrentSiteTelemetryProvider(
        TelemetrySourceSelector sourceSelector,
        ResilientCurrentSiteTelemetryProvider victronCurrentSiteTelemetryProvider,
        HagerEnergyCurrentSiteTelemetryProvider hagerEnergyCurrentSiteTelemetryProvider)
    {
        this.sourceSelector = sourceSelector;
        this.victronCurrentSiteTelemetryProvider = victronCurrentSiteTelemetryProvider;
        this.hagerEnergyCurrentSiteTelemetryProvider = hagerEnergyCurrentSiteTelemetryProvider;
    }

    public async Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        var gridImportSource = await sourceSelector.GetGridImportSourceAsync(cancellationToken);
        var pvProductionSource = await sourceSelector.GetPvProductionSourceAsync(cancellationToken);
        var victronTelemetry = gridImportSource == TelemetrySourceSelector.VictronMqttProvider ||
            pvProductionSource == TelemetrySourceSelector.VictronMqttProvider
            ? await victronCurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken)
            : null;
        var hagerGridImport = gridImportSource == TelemetrySourceSelector.HagerEnergyApiProvider
            ? await hagerEnergyCurrentSiteTelemetryProvider.GetGridImportWattsAsync(cancellationToken)
            : ((int GridImportWatts, DateTimeOffset MeasuredAtUtc)?)null;
        var hagerPvProduction = pvProductionSource == TelemetrySourceSelector.HagerEnergyApiProvider
            ? await hagerEnergyCurrentSiteTelemetryProvider.GetPvProductionWattsAsync(cancellationToken)
            : ((int PvProductionWatts, DateTimeOffset MeasuredAtUtc)?)null;
        var gridImportWatts = SelectGridImportWatts(gridImportSource, victronTelemetry, hagerGridImport);
        var pvProductionWatts = SelectPvProductionWatts(pvProductionSource, victronTelemetry, hagerPvProduction);
        var measuredAtUtc = SelectMeasuredAtUtc(gridImportSource, pvProductionSource, victronTelemetry, hagerGridImport, hagerPvProduction);

        return new CurrentSiteTelemetry(gridImportWatts, pvProductionWatts, measuredAtUtc);
    }

    private static int SelectGridImportWatts(
        string source,
        CurrentSiteTelemetry? victronTelemetry,
        (int GridImportWatts, DateTimeOffset MeasuredAtUtc)? hagerGridImport)
    {
        return source switch
        {
            TelemetrySourceSelector.HagerEnergyApiProvider => hagerGridImport!.Value.GridImportWatts,
            TelemetrySourceSelector.VictronMqttProvider => victronTelemetry!.CurrentGridImportWatts,
            _ => throw new InvalidOperationException($"Die Telemetriequelle '{source}' fuer den Netzbezug ist nicht bekannt.")
        };
    }

    private static int SelectPvProductionWatts(
        string source,
        CurrentSiteTelemetry? victronTelemetry,
        (int PvProductionWatts, DateTimeOffset MeasuredAtUtc)? hagerPvProduction)
    {
        return source switch
        {
            TelemetrySourceSelector.HagerEnergyApiProvider => hagerPvProduction!.Value.PvProductionWatts,
            TelemetrySourceSelector.VictronMqttProvider => victronTelemetry!.CurrentPvProductionWatts,
            _ => throw new InvalidOperationException($"Die Telemetriequelle '{source}' fuer die PV-Leistung ist nicht bekannt.")
        };
    }

    private static DateTimeOffset SelectMeasuredAtUtc(
        string gridImportSource,
        string pvProductionSource,
        CurrentSiteTelemetry? victronTelemetry,
        (int GridImportWatts, DateTimeOffset MeasuredAtUtc)? hagerGridImport,
        (int PvProductionWatts, DateTimeOffset MeasuredAtUtc)? hagerPvProduction)
    {
        var gridMeasuredAtUtc = gridImportSource == TelemetrySourceSelector.HagerEnergyApiProvider
            ? hagerGridImport!.Value.MeasuredAtUtc
            : victronTelemetry!.MeasuredAtUtc;
        var pvMeasuredAtUtc = pvProductionSource == TelemetrySourceSelector.HagerEnergyApiProvider
            ? hagerPvProduction!.Value.MeasuredAtUtc
            : victronTelemetry!.MeasuredAtUtc;

        return gridMeasuredAtUtc <= pvMeasuredAtUtc ? gridMeasuredAtUtc : pvMeasuredAtUtc;
    }
}
