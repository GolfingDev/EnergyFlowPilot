using System.Net;
using System.Net.Http.Headers;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Homematic;
using TibberVictronController.Dal.Tests.TestDoubles;

namespace TibberVictronController.Dal.Tests;

public sealed class HomematicXmlApiClientTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetCurrentSnapshotAsyncReadsOnlyListEndpointsAndDecodesLatin1()
    {
        var httpHandler = new XmlFileHttpMessageHandler();
        var client = CreateClient(httpHandler, isEnabled: true, apiToken: "test-token");

        var snapshot = await client.GetCurrentSnapshotAsync(UpdatedAtUtc);

        Assert.Equal(
            new[]
            {
                "http://homematic-raspi/config/xmlapi/statelist.cgi?sid=test-token",
                "http://homematic-raspi/config/xmlapi/devicelist.cgi?sid=test-token"
            },
            httpHandler.Requests.Select(request => request.RequestUri!.ToString()));
        Assert.All(httpHandler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains(snapshot.Samples, sample => sample.DeviceName == "Heizung Büro");
    }

    [Fact]
    public async Task GetCurrentSnapshotAsyncSendsNoRequestWithoutToken()
    {
        var httpHandler = new XmlFileHttpMessageHandler();
        var client = CreateClient(httpHandler, isEnabled: true, apiToken: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCurrentSnapshotAsync(UpdatedAtUtc));

        Assert.Empty(httpHandler.Requests);
    }

    private static HomematicXmlApiClient CreateClient(XmlFileHttpMessageHandler httpHandler, bool isEnabled, string? apiToken)
    {
        var settingsStore = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc));
        settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.HomematicEnabledKey,
            isEnabled ? "true" : "false",
            ControllerSettingSensitivity.Normal,
            UpdatedAtUtc)).GetAwaiter().GetResult();
        settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.HomematicApiTokenKey,
            apiToken,
            ControllerSettingSensitivity.Sensitive,
            UpdatedAtUtc)).GetAwaiter().GetResult();

        return new HomematicXmlApiClient(new HttpClient(httpHandler), new DatabaseHomematicSettingsProvider(settingsStore));
    }

    /// <summary>
    /// Serves the captured CCU responses as raw ISO-8859-1 bytes, exactly like the real XML-API.
    /// </summary>
    private sealed class XmlFileHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var fileName = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath) + ".xml";
            var content = new ByteArrayContent(await File.ReadAllBytesAsync(HomematicXmlParserTests.GetTestDataPath(fileName), cancellationToken));
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=iso-8859-1");

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
