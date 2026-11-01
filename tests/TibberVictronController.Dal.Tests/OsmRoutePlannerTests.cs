using System.Net;
using System.Text;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Routing;

namespace TibberVictronController.Dal.Tests;

public sealed class OsmRoutePlannerTests
{
    [Fact]
    public async Task DestinationIsGeocodedAndRoutedFromHome()
    {
        var handler = new HostHandler(
            """[{"lat":"48.1371","lon":"11.5754","display_name":"München, Bayern, Deutschland"}]""",
            """{"code":"Ok","routes":[{"distance":584321.0,"duration":20400.0}]}""");
        var planner = new OsmRoutePlanner(new HttpClient(handler));

        var route = await planner.EstimateAsync(new GeoPoint(52.52, 13.405), "München Marienplatz");

        Assert.Equal(584.3, route.DistanceKm);
        Assert.Equal(340, route.DurationMinutes);
        Assert.Equal("München, Bayern, Deutschland", route.ResolvedAddress);
        Assert.Contains("q=M%C3%BCnchen%20Marienplatz", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("bounded=1", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("13.405,52.52;11.5754,48.1371", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.All(handler.Requests, request => Assert.Contains("EnergyFlowPilot", request.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task UnknownDestinationGivesAGermanHint()
    {
        var planner = new OsmRoutePlanner(new HttpClient(new HostHandler("[]", "{}")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => planner.EstimateAsync(new GeoPoint(52.52, 13.405), "Xyzzy"));

        Assert.Contains("nicht gefunden", exception.Message);
    }

    [Fact]
    public async Task NearbyMatchWinsAndTheWholeMapIsOnlyTheFallback()
    {
        var handler = new LocalFirstHandler();
        var planner = new OsmRoutePlanner(new HttpClient(handler));

        var route = await planner.EstimateAsync(new GeoPoint(50.55, 9.68), "Lidl");

        Assert.Equal("Lidl, Fulda", route.ResolvedAddress);
        Assert.Contains("viewbox=8.93,51.05,10.43,50.05", handler.Requests[0].RequestUri!.Query);
        Assert.Equal(2, handler.Requests.Count); // local search hit, then the route; no nationwide search
    }

    private sealed class LocalFirstHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var query = request.RequestUri!.Query;
            var body = request.RequestUri.Host.StartsWith("nominatim", StringComparison.Ordinal)
                ? query.Contains("bounded=1")
                    // The better-known store comes first, the nearer one second: the nearer one must win.
                    ? """[{"lat":"50.12","lon":"9.10","display_name":"Lidl, weiter weg"},{"lat":"50.56","lon":"9.70","display_name":"Lidl, Fulda"}]"""
                    : """[{"lat":"48.0","lon":"11.0","display_name":"Lidl, weit weg"}]"""
                : """{"code":"Ok","routes":[{"distance":3200.0,"duration":420.0}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task ShortestOfTheNearlyEquallyFastRoutesIsTaken()
    {
        // Operator 2026-11-01, Hattenhof to Neuhof: 12 km via A66 in 12.2 min against 5.7 km via K72 in 12.5 min;
        // a 30 km route twice as slow is never taken.
        var handler = new HostHandler("[]",
            """{"code":"Ok","routes":[{"distance":12000.0,"duration":732.0},{"distance":5700.0,"duration":750.0},{"distance":3000.0,"duration":1500.0}]}""");
        var planner = new OsmRoutePlanner(new HttpClient(handler));

        var route = await planner.RouteToAsync(new GeoPoint(50.466, 9.677), new GeoPoint(50.451, 9.620), "Zollweg, 36119 Neuhof");

        Assert.Equal(5.7, route.DistanceKm);
        Assert.Equal(12, route.DurationMinutes); // 12.5 min, rounded to even
        Assert.Contains("alternatives=3", handler.Requests[0].RequestUri!.Query);
    }

    private sealed class HostHandler(string geocodeBody, string routeBody) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.RequestUri!.Host.StartsWith("nominatim", StringComparison.Ordinal) ? geocodeBody : routeBody;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
