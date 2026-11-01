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
