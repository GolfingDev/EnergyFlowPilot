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
    public async Task GetCurrentValuesAsyncUsesConfiguredAccessTokenAndMapsCurrentEnergyValues()
    {
        var httpHandler = new RecordingHttpMessageHandler("""
            {
              "data": {
                "gridImportWatts": 345,
                "pvProductionWatts": 2100,
                "batterySocPercent": 67.5
              }
            }
            """);
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "access-token", refreshToken: null);
        var settingsProvider = new DatabaseHagerEnergySettingsProvider(settingsStore);
        var client = new HagerEnergyApiClient(httpClient, settingsProvider, settingsStore);

        var currentValues = await client.GetCurrentValuesAsync();

        Assert.Equal(HttpMethod.Get, httpHandler.LastRequest?.Method);
        Assert.Equal("https://api.hagerenergy.com/v1/installations/installation-a/energy/current", httpHandler.LastRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", httpHandler.LastRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("access-token", httpHandler.LastRequest?.Headers.Authorization?.Parameter);
        Assert.Equal(345m, currentValues.GridImportWatts);
        Assert.Equal(2100m, currentValues.PvProductionWatts);
        Assert.Equal(67.5m, currentValues.BatterySocPercent);
    }

    [Fact]
    public async Task GetCurrentValuesAsyncSendsApiKeyHeaderWhenConfigured()
    {
        var httpHandler = new RecordingHttpMessageHandler("""
            {
              "data": {
                "gridImportWatts": 345,
                "pvProductionWatts": 2100,
                "batterySocPercent": 67.5
              }
            }
            """);
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "access-token", refreshToken: null);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyApiKeyKey, "api-key", ControllerSettingSensitivity.Sensitive);
        var settingsProvider = new DatabaseHagerEnergySettingsProvider(settingsStore);
        var client = new HagerEnergyApiClient(httpClient, settingsProvider, settingsStore);

        await client.GetCurrentValuesAsync();

        Assert.NotNull(httpHandler.LastRequest);
        Assert.True(httpHandler.LastRequest.Headers.TryGetValues("api_key", out var values));
        Assert.Equal("api-key", values.Single());
    }

    [Fact]
    public async Task GetCurrentValuesAsyncSupportsConfiguredJsonPathsWithArrayIndexes()
    {
        var httpHandler = new RecordingHttpMessageHandler("""
            {
              "flows": [
                { "value": 345 },
                { "value": 2100 },
                { "value": 67.5 }
              ]
            }
            """);
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "access-token", refreshToken: null);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyGridImportJsonPathKey, "flows.0.value", ControllerSettingSensitivity.Normal);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyPvProductionJsonPathKey, "flows.1.value", ControllerSettingSensitivity.Normal);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyBatterySocJsonPathKey, "flows.2.value", ControllerSettingSensitivity.Normal);
        var settingsProvider = new DatabaseHagerEnergySettingsProvider(settingsStore);
        var client = new HagerEnergyApiClient(httpClient, settingsProvider, settingsStore);

        var currentValues = await client.GetCurrentValuesAsync();

        Assert.Equal(345m, currentValues.GridImportWatts);
        Assert.Equal(2100m, currentValues.PvProductionWatts);
        Assert.Equal(67.5m, currentValues.BatterySocPercent);
    }

    [Fact]
    public async Task GetPvProductionWattsAsyncDoesNotRequireGridOrBatteryValues()
    {
        var httpHandler = new RecordingHttpMessageHandler("""
            {
              "data": {
                "pvProductionWatts": 2100
              }
            }
            """);
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "access-token", refreshToken: null);
        var settingsProvider = new DatabaseHagerEnergySettingsProvider(settingsStore);
        var client = new HagerEnergyApiClient(httpClient, settingsProvider, settingsStore);

        var pvProduction = await client.GetPvProductionWattsAsync();

        Assert.Equal(2100m, pvProduction.Value);
    }

    [Fact]
    public async Task GetCurrentValuesAsyncRefreshesAccessTokenWhenOnlyRefreshTokenIsConfigured()
    {
        var httpHandler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "access_token": "fresh-access-token", "refresh_token": "next-refresh-token" }""", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "data": {
                        "gridImportWatts": { "value": 120 },
                        "pvProductionWatts": { "value": 1500 },
                        "batterySocPercent": { "value": 52 }
                      }
                    }
                    """, Encoding.UTF8, "application/json")
            });
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: null, refreshToken: "refresh-token");
        var settingsProvider = new DatabaseHagerEnergySettingsProvider(settingsStore);
        var client = new HagerEnergyApiClient(httpClient, settingsProvider, settingsStore);

        var currentValues = await client.GetCurrentValuesAsync();

        Assert.Equal(2, httpHandler.Requests.Count);
        Assert.Equal(HttpMethod.Post, httpHandler.Requests[0].Method);
        Assert.Equal("Basic", httpHandler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal(HttpMethod.Get, httpHandler.Requests[1].Method);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "fresh-access-token").ToString(), httpHandler.Requests[1].Headers.Authorization?.ToString());
        Assert.Equal(120m, currentValues.GridImportWatts);
        Assert.Equal(1500m, currentValues.PvProductionWatts);
        Assert.Equal(52m, currentValues.BatterySocPercent);

        var savedRefreshToken = await settingsStore.GetSettingAsync(ControllerSettingDefaults.HagerEnergyRefreshTokenKey);
        Assert.Equal("next-refresh-token", savedRefreshToken?.Value);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsyncStoresReturnedTokensAndClearsOAuthState()
    {
        var httpHandler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "access_token": "code-access-token", "refresh_token": "code-refresh-token" }""", Encoding.UTF8, "application/json")
            });
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: null, refreshToken: null);
        ReplaceStoredSetting(settingsStore, ControllerSettingDefaults.HagerEnergyOAuthStateKey, "expected-state", ControllerSettingSensitivity.Sensitive);
        var settingsProvider = new DatabaseHagerEnergySettingsProvider(settingsStore);
        var client = new HagerEnergyApiClient(httpClient, settingsProvider, settingsStore);

        await client.ExchangeAuthorizationCodeAsync("authorization-code", "expected-state");

        Assert.Single(httpHandler.Requests);
        Assert.Equal(HttpMethod.Post, httpHandler.Requests[0].Method);
        var tokenRequestBody = httpHandler.RequestBodies[0];
        Assert.Contains("grant_type=authorization_code", tokenRequestBody);
        Assert.Contains("code=authorization-code", tokenRequestBody);

        var savedAccessToken = await settingsStore.GetSettingAsync(ControllerSettingDefaults.HagerEnergyAccessTokenKey);
        var savedRefreshToken = await settingsStore.GetSettingAsync(ControllerSettingDefaults.HagerEnergyRefreshTokenKey);
        var savedOAuthState = await settingsStore.GetSettingAsync(ControllerSettingDefaults.HagerEnergyOAuthStateKey);
        Assert.Equal("code-access-token", savedAccessToken?.Value);
        Assert.Equal("code-refresh-token", savedRefreshToken?.Value);
        Assert.False(savedOAuthState?.IsConfigured);
    }

    private static FakeControllerSettingStore CreateSettingsStore(
        string? accessToken,
        string? refreshToken)
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc).ToList();

        ReplaceSetting(settings, ControllerSettingDefaults.HagerEnergyAccessTokenKey, accessToken, ControllerSettingSensitivity.Sensitive);
        ReplaceSetting(settings, ControllerSettingDefaults.HagerEnergyRefreshTokenKey, refreshToken, ControllerSettingSensitivity.Sensitive);
        ReplaceSetting(settings, ControllerSettingDefaults.HagerEnergyAuthorizationEndpointKey, "https://auth.hagerenergy.com/realms/customer/protocol/openid-connect/auth", ControllerSettingSensitivity.Normal);
        ReplaceSetting(settings, ControllerSettingDefaults.HagerEnergyTokenEndpointKey, "https://auth.hagerenergy.com/realms/customer/protocol/openid-connect/token", ControllerSettingSensitivity.Normal);
        ReplaceSetting(settings, ControllerSettingDefaults.HagerEnergyClientIdKey, "client-id", ControllerSettingSensitivity.Sensitive);
        ReplaceSetting(settings, ControllerSettingDefaults.HagerEnergyClientSecretKey, "client-secret", ControllerSettingSensitivity.Sensitive);
        ReplaceSetting(settings, ControllerSettingDefaults.HagerEnergyInstallationIdKey, "installation-a", ControllerSettingSensitivity.Sensitive);

        return new FakeControllerSettingStore(settings);
    }

    private static void ReplaceSetting(
        List<ControllerSetting> settings,
        string key,
        string? value,
        ControllerSettingSensitivity sensitivity)
    {
        settings.RemoveAll(setting => setting.Key == key);
        settings.Add(new ControllerSetting(key, value, sensitivity, UpdatedAtUtc));
    }

    private static void ReplaceStoredSetting(
        FakeControllerSettingStore settingsStore,
        string key,
        string value,
        ControllerSettingSensitivity sensitivity)
    {
        settingsStore.SaveSettingAsync(new ControllerSetting(key, value, sensitivity, UpdatedAtUtc)).GetAwaiter().GetResult();
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
