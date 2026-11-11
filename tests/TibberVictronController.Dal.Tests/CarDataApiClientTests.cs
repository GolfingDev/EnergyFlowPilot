using System.Net;
using System.Text;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.BmwCarData;

namespace TibberVictronController.Dal.Tests;

public sealed class CarDataApiClientTests
{
    [Fact]
    public async Task ExistingContainerIsReusedOtherwiseCreated()
    {
        var reuse = new RoutingHandler(request => request.Method == HttpMethod.Get
            ? $$"""{"containers":[{"containerId":"c-1","name":"{{CarDataApiClient.ContainerName}}"}]}""" : "{}");
        Assert.Equal("c-1", await new CarDataApiClient(new HttpClient(reuse)).EnsureContainerAsync("token", ["a"], CancellationToken.None));
        Assert.Single(reuse.Requests);

        var create = new RoutingHandler(request => request.Method == HttpMethod.Get ? """{"containers":[]}""" : """{"containerId":"c-2"}""");
        Assert.Equal("c-2", await new CarDataApiClient(new HttpClient(create)).EnsureContainerAsync("token", ["a", "b"], CancellationToken.None));
        Assert.Contains("\"technicalDescriptors\":[\"a\",\"b\"]", create.Bodies[1]);
        Assert.All(create.Requests, request => Assert.Equal("v1", request.Headers.GetValues("x-version").Single()));
    }

    [Fact]
    public async Task TelematicDataBecomesAStreamShapedMessage()
    {
        var handler = new RoutingHandler(_ => """
            {"telematicData":{"vehicle.powertrain.electric.battery.stateOfCharge.displayed":{"value":"64","unit":"%","timestamp":"2026-11-01T08:00:00Z"}}}
            """);

        var json = await new CarDataApiClient(new HttpClient(handler)).GetTelematicDataAsync("token", "WBY51HD030FV70436", "c-1", CancellationToken.None);
        var car = CarDataMessageParser.Apply(CarSnapshot.Empty, json, null, DateTimeOffset.UtcNow)!;

        Assert.Equal(64, car.StateOfChargePercent);
        Assert.Equal("WBY51HD030FV70436", car.Vin);
        Assert.Contains("/customers/vehicles/WBY51HD030FV70436/telematicData?containerId=c-1", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer token", handler.Requests[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task PrimaryVehicleIsFoundAndQuotaErrorsAreRecognised()
    {
        var mappings = new RoutingHandler(_ => """[{"vin":"WBA-SECONDARY","mappingType":"SECONDARY"},{"vin":"WBY-PRIMARY","mappingType":"PRIMARY"}]""");
        Assert.Equal("WBY-PRIMARY", await new CarDataApiClient(new HttpClient(mappings)).GetPrimaryVinAsync("token", CancellationToken.None));

        var quota = new RoutingHandler(_ => "{}", HttpStatusCode.TooManyRequests);
        await Assert.ThrowsAsync<CarDataQuotaExceededException>(() =>
            new CarDataApiClient(new HttpClient(quota)).GetTelematicDataAsync("token", "VIN", "c-1", CancellationToken.None));
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, string> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") };
        }
    }
}
