using System.Net;
using System.Security.Cryptography;
using System.Text;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.BmwCarData;
using TibberVictronController.Dal.Tests.TestDoubles;

namespace TibberVictronController.Dal.Tests;

public sealed class CarDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StreamMessageUpdatesOnlyTheReportedValues()
    {
        var before = CarSnapshot.Empty with { RemainingRangeKm = 120, Vin = "WBA00000000000001" };
        const string message = """
            {"vin":"WBA00000000000001","data":{
              "vehicle.drivetrain.batteryManagement.header":{"value":"63","unit":"%","timestamp":"2026-11-01T08:55:00Z"},
              "vehicle.powertrain.electric.battery.stateOfCharge.target":{"value":80,"unit":"%"},
              "vehicle.powertrain.tractionBattery.charging.port.anyPosition.isPlugged":{"value":true},
              "vehicle.drivetrain.electricEngine.charging.status":{"value":"CHARGINGACTIVE"},
              "vehicle.cabin.unknown":{"value":1}}}
            """;

        var after = CarDataMessageParser.Apply(before, message, null, Now)!;

        Assert.Equal(63, after.StateOfChargePercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 8, 55, 0, TimeSpan.Zero), after.StateOfChargeAtUtc);
        Assert.Equal(80, after.TargetStateOfChargePercent);
        Assert.True(after.IsPlugged);
        Assert.Equal("CHARGINGACTIVE", after.ChargingStatus);
        Assert.Equal(120, after.RemainingRangeKm);
        Assert.Equal(Now, after.LastMessageAtUtc);
    }

    [Fact]
    public void MessagesForOtherCarsAndInvalidValuesAreIgnored()
    {
        const string otherCar = """{"vin":"WBA99999999999999","data":{"vehicle.drivetrain.batteryManagement.header":{"value":50}}}""";
        const string implausible = """{"vin":"WBA00000000000001","data":{"vehicle.drivetrain.batteryManagement.header":{"value":140}}}""";

        Assert.Null(CarDataMessageParser.Apply(CarSnapshot.Empty, otherCar, "WBA00000000000001", Now));
        Assert.Null(CarDataMessageParser.Apply(CarSnapshot.Empty, "kein json", null, Now));
        Assert.Null(CarDataMessageParser.Apply(CarSnapshot.Empty, implausible, null, Now)!.StateOfChargePercent);
    }

    [Fact]
    public async Task DeviceFlowUsesPkceAndReturnsTheUserCode()
    {
        var handler = new RecordingHttpMessageHandler("""
            {"device_code":"dc","user_code":"ABCD-EFGH","verification_uri":"https://customer.bmwgroup.com/oneid/link","verification_uri_complete":"https://customer.bmwgroup.com/oneid/link?code=ABCD-EFGH","interval":5,"expires_in":300}
            """);
        var client = new CarDataAuthClient(new HttpClient(handler));

        var flow = await client.StartAsync("client-1", Now, CancellationToken.None);

        var form = ParseForm(handler.LastRequestBody!);
        Assert.Equal(CarDataAuthClient.DeviceCodeUrl, handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("S256", form["code_challenge_method"]);
        Assert.Equal(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(flow.CodeVerifier))), form["code_challenge"]);
        Assert.Contains("cardata:streaming:read", form["scope"]);
        Assert.Equal("ABCD-EFGH", flow.UserCode);
        Assert.EndsWith("code=ABCD-EFGH", flow.VerificationUrl);
        Assert.Equal(Now.AddSeconds(300), flow.ExpiresAtUtc);
    }

    [Fact]
    public async Task PendingAndCompletedPollsAreDistinguished()
    {
        var flow = new CarDataDeviceFlow("dc", "verifier", "CODE", "https://x", TimeSpan.FromSeconds(5), Now.AddMinutes(5));
        var pending = new CarDataAuthClient(new HttpClient(new RecordingHttpMessageHandler("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest)));
        var done = new CarDataAuthClient(new HttpClient(new RecordingHttpMessageHandler("""
            {"gcid":"g-1","id_token":"id","access_token":"a","refresh_token":"r","expires_in":3600}
            """)));

        Assert.Equal(CarDataPollOutcome.Pending, (await pending.PollAsync("client-1", flow, CancellationToken.None)).Outcome);
        var completed = await done.PollAsync("client-1", flow, CancellationToken.None);
        Assert.Equal(CarDataPollOutcome.Completed, completed.Outcome);
        Assert.Equal("g-1", completed.Tokens!.Gcid);
        Assert.Equal("r", completed.Tokens.RefreshToken);
    }

    [Fact]
    public async Task RejectedRefreshRequiresANewSignInWithoutLeakingTokens()
    {
        var client = new CarDataAuthClient(new HttpClient(new RecordingHttpMessageHandler(
            """{"error":"invalid_grant","error_description":"expired","refresh_token":"secret-value"}""", HttpStatusCode.BadRequest)));

        var exception = await Assert.ThrowsAsync<CarDataSignInRequiredException>(() => client.RefreshAsync("client-1", "old", "g-1", CancellationToken.None));

        Assert.Contains("invalid_grant", exception.Message);
        Assert.DoesNotContain("secret-value", exception.Message);
    }

    [Fact]
    public async Task RefreshKeepsTheKnownAccountIdWhenBmwOmitsIt()
    {
        var client = new CarDataAuthClient(new HttpClient(new RecordingHttpMessageHandler("""{"id_token":"id","refresh_token":"r2","expires_in":3600}""")));

        var tokens = await client.RefreshAsync("client-1", "r1", "g-1", CancellationToken.None);

        Assert.Equal("g-1", tokens.Gcid);
        Assert.Equal("r2", tokens.RefreshToken);
    }

    private static Dictionary<string, string> ParseForm(string body) => body.Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1].Replace('+', ' ')));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
