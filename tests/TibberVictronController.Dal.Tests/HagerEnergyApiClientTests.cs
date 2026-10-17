using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.HagerEnergy;
using TibberVictronController.Dal.Tests.TestDoubles;

namespace TibberVictronController.Dal.Tests;

public sealed class HagerEnergyApiClientTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetCurrentValuesAsyncRequestsClientCredentialsTokenAndMapsCurrentEnergyValues()
    {
        var httpHandler = new QueueHttpMessageHandler(
            CreateTokenResponse("fresh-access-token"),
            CreateJsonResponse("""
                {
                  "data": {
                    "gridImportWatts": 345,
                    "pvProductionWatts": 2100,
                    "batterySocPercent": 67.5
                  }
                }
                """));
        var client = CreateClient(httpHandler, CreateSettingsStore());

        var currentValues = await client.GetCurrentValuesAsync();

        Assert.Equal(2, httpHandler.Requests.Count);
        Assert.Equal(HttpMethod.Post, httpHandler.Requests[0].Method);
        Assert.Equal("https://auth.hagerenergy.com/realms/customer/protocol/openid-connect/token", httpHandler.Requests[0].RequestUri?.ToString());
        Assert.Contains("grant_type=client_credentials", httpHandler.RequestBodies[0]);
        Assert.Contains("client_id=client-id", httpHandler.RequestBodies[0]);
        Assert.Equal(HttpMethod.Get, httpHandler.Requests[1].Method);
        Assert.Equal("https://api.hagerenergy.com/v1/installations/installation-a/energy/current", httpHandler.Requests[1].RequestUri?.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "fresh-access-token").ToString(), httpHandler.Requests[1].Headers.Authorization?.ToString());
        Assert.Equal(345m, currentValues.GridImportWatts);
        Assert.Equal(2100m, currentValues.PvProductionWatts);
        Assert.Equal(67.5m, currentValues.BatterySocPercent);
        Assert.Null(currentValues.BatteryPowerWatts);
    }

    [Fact]
    public async Task GetCurrentValuesAsyncReusesCachedAccessToken()
    {
        const string currentValuesJson = """{ "data": { "gridImportWatts": 1, "pvProductionWatts": 2, "batterySocPercent": 3 } }""";
        var httpHandler = new QueueHttpMessageHandler(
            CreateTokenResponse("cached-access-token"),
            CreateJsonResponse(currentValuesJson),
            CreateJsonResponse(currentValuesJson));
        var client = CreateClient(httpHandler, CreateSettingsStore());

        await client.GetCurrentValuesAsync();
        await client.GetCurrentValuesAsync();

        Assert.Equal(3, httpHandler.Requests.Count);
        Assert.Single(httpHandler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task GetCurrentValuesAsyncSupportsConfiguredJsonPathsWithArrayIndexes()
    {
        var httpHandler = new QueueHttpMessageHandler(
            CreateTokenResponse("access-token"),
            CreateJsonResponse("""
                {
                  "flows": [
                    { "value": 345 },
                    { "value": 2100 },
                    { "value": 67.5 }
                  ]
                }
                """));
        var settingsStore = CreateSettingsStore();
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyGridImportJsonPathKey, "flows.0.value", ControllerSettingSensitivity.Normal);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyPvProductionJsonPathKey, "flows.1.value", ControllerSettingSensitivity.Normal);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyBatterySocJsonPathKey, "flows.2.value", ControllerSettingSensitivity.Normal);
        var client = CreateClient(httpHandler, settingsStore);

        var currentValues = await client.GetCurrentValuesAsync();

        Assert.Equal(345m, currentValues.GridImportWatts);
        Assert.Equal(2100m, currentValues.PvProductionWatts);
        Assert.Equal(67.5m, currentValues.BatterySocPercent);
    }

    [Fact]
    public async Task GetCurrentValuesAsyncSendsNoRequestWithoutCredentials()
    {
        var httpHandler = new QueueHttpMessageHandler();
        var settingsStore = CreateSettingsStore();
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyClientSecretKey, null, ControllerSettingSensitivity.Sensitive);
        var client = CreateClient(httpHandler, settingsStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCurrentValuesAsync());

        Assert.Empty(httpHandler.Requests);
    }

    private static HagerEnergyApiClient CreateClient(QueueHttpMessageHandler httpHandler, FakeControllerSettingStore settingsStore)
    {
        // A fresh token cache per test keeps the cached-token behaviour isolated.
        return new HagerEnergyApiClient(
            new HttpClient(httpHandler),
            new DatabaseHagerEnergySettingsProvider(settingsStore),
            new HagerEnergyTokenCache());
    }

    private static FakeControllerSettingStore CreateSettingsStore()
    {
        var settingsStore = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc));
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyClientIdKey, "client-id", ControllerSettingSensitivity.Sensitive);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyClientSecretKey, "client-secret", ControllerSettingSensitivity.Sensitive);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyInstallationIdKey, "installation-a", ControllerSettingSensitivity.Sensitive);

        return settingsStore;
    }

    private static void ReplaceStoredSetting(
        FakeControllerSettingStore settingsStore,
        string key,
        string? value,
        ControllerSettingSensitivity sensitivity)
    {
        settingsStore.SaveSettingAsync(new ControllerSetting(key, value, sensitivity, UpdatedAtUtc)).GetAwaiter().GetResult();
    }

    private static HttpResponseMessage CreateTokenResponse(string accessToken)
    {
        return CreateJsonResponse($$"""{ "access_token": "{{accessToken}}", "expires_in": 300 }""");
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;

        public QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string?> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return responses.Dequeue();
        }
    }
}
