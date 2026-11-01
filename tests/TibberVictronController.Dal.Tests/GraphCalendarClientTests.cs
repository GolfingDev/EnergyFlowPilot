using System.Net;
using System.Text;
using TibberVictronController.Dal.Calendar;

namespace TibberVictronController.Dal.Tests;

public sealed class GraphCalendarClientTests
{
    private static readonly GraphCredentials Credentials = new("tenant-1", "client-1", "secret-value");

    [Fact]
    public async Task EventsAreReadInUtcWithLocationOrAddress()
    {
        var handler = new GraphHandler("""{"access_token":"token-1","expires_in":3599}""", """
            {"value":[
              {"id":"a","subject":"Kunde","isAllDay":false,"isCancelled":false,"isOnlineMeeting":false,
               "start":{"dateTime":"2026-11-03T08:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-11-03T10:00:00.0000000","timeZone":"UTC"},
               "location":{"displayName":"Leipzig"}},
              {"id":"b","subject":"Messe","isAllDay":true,"isCancelled":false,"isOnlineMeeting":false,
               "start":{"dateTime":"2026-11-05T00:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-11-07T00:00:00.0000000","timeZone":"UTC"},
               "location":{"displayName":"","address":{"street":"Messeallee 1","postalCode":"04356","city":"Leipzig"}}}]}
            """);
        var client = new GraphCalendarClient(new HttpClient(handler));

        var events = await client.GetEventsAsync(Credentials, "torsten@example.com", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14), CancellationToken.None);

        Assert.Equal(2, events.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero), events[0].StartUtc);
        Assert.Equal("Leipzig", events[0].Location);
        Assert.True(events[1].IsAllDay);
        Assert.Equal("Messeallee 1, 04356, Leipzig", events[1].Location);
        Assert.Contains("client_credentials", handler.TokenRequestBody);
        Assert.Contains("/users/torsten%40example.com/calendarView", handler.CalendarRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer token-1", handler.CalendarRequest.Headers.Authorization!.ToString());
        Assert.Contains("outlook.timezone=\"UTC\"", handler.CalendarRequest.Headers.GetValues("Prefer").Single());
    }

    [Fact]
    public async Task MissingPermissionGivesAGermanHintWithoutSecrets()
    {
        var client = new GraphCalendarClient(new HttpClient(new GraphHandler("""{"access_token":"token-1"}""", "{}", HttpStatusCode.Forbidden)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetEventsAsync(Credentials, "torsten@example.com", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None));

        Assert.Contains("Calendars.Read", exception.Message);
        Assert.DoesNotContain("secret-value", exception.Message);
    }

    private sealed class GraphHandler(string tokenBody, string calendarBody, HttpStatusCode calendarStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string TokenRequestBody { get; private set; } = string.Empty;
        public HttpRequestMessage? CalendarRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                TokenRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenBody, Encoding.UTF8, "application/json") };
            }

            CalendarRequest = request;
            return new HttpResponseMessage(calendarStatus) { Content = new StringContent(calendarBody, Encoding.UTF8, "application/json") };
        }
    }
}
