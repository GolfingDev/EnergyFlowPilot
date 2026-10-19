using TibberVictronController.Dal.HagerEnergy;

namespace TibberVictronController.Api.Tests;

public sealed class HagerEnergyPvProductionTests
{
    [Fact]
    public void TotalPvIncludesExternalInverterFromProductionMeter()
    {
        // Response of 2026-10-19 10:24: the app shows "PV 664 W" and "Ext. PV" through the production meter.
        const string response = """
            {"status":"success","data":{"id":1000012436,"time":"2026-10-19T10:24:03.000+02:00","pvProduction":664,"production":664,
            "externalProduction":0,"gridPower":-162,"directConsumption":0,"consumption":1122,"batteryChargePower":2916,
            "batteryStateOfCharge":26,"meters":[{"id":"1","power":-3536,"meterType":"production"}],"chargingStations":[]}}
            """;

        Assert.Equal(4200m, HagerEnergyApiClient.CalculateTotalPvProductionWatts(response));
    }

    [Fact]
    public void ExternalProductionFieldIsNotAddedTwiceToTheMeter()
    {
        const string response = """
            {"data":{"pvProduction":500,"externalProduction":3000,"meters":[{"power":-3000,"meterType":"production"},{"power":800,"meterType":"consumption"}]}}
            """;

        Assert.Equal(3500m, HagerEnergyApiClient.CalculateTotalPvProductionWatts(response));
    }
}
