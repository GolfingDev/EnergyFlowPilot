using System.Net;
using System.Text;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Routing;

namespace TibberVictronController.Dal.Tests;

public sealed class PhotonPlaceSearchTests
{
    [Fact]
    public async Task SuggestionsAreReadableAndBiasedTowardsHome()
    {
        var handler = new Handler("""
            {"features":[
              {"geometry":{"coordinates":[9.8709,50.5932]},"properties":{"name":"Hofbieber","postcode":"36145","city":"Hofbieber","country":"Deutschland"}},
              {"geometry":{"coordinates":[9.9532,50.5251]},"properties":{"name":"Jugendherberge","street":"Milseburgstraße","housenumber":"1","postcode":"36145","city":"Hofbieber","country":"Deutschland"}},
              {"geometry":{"coordinates":[16.37,48.21]},"properties":{"name":"Wien","country":"Österreich"}}]}
            """);

        var places = await new PhotonPlaceSearch(new HttpClient(handler)).SuggestAsync("Hofbieb", new GeoPoint(50.5558, 9.6808));

        Assert.Equal("Hofbieber, 36145 Hofbieber", places[0].Label);
        Assert.Equal("Jugendherberge, Milseburgstraße 1, 36145 Hofbieber", places[1].Label);
        Assert.Equal("Wien, Österreich", places[2].Label);
        Assert.Equal(50.5932, places[0].Latitude);
        Assert.Contains("lat=50.5558&lon=9.6808", handler.Request!.RequestUri!.Query);
        Assert.Contains("EnergyFlowPilot", handler.Request.Headers.UserAgent.ToString());
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
