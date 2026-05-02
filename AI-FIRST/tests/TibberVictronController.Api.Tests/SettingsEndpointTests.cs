using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Settings;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Tests;

public sealed class SettingsEndpointTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSettingsAsyncReturnsFrontendSafeSettingDtos()
    {
        var service = new FakeControllerSettingsService(new[]
        {
            new ControllerSetting(ControllerSettingDefaults.BatteryTotalCapacityKwhKey, "12", ControllerSettingSensitivity.Normal, UpdatedAtUtc),
            new ControllerSetting(ControllerSettingDefaults.TibberAccessTokenKey, "secret-token", ControllerSettingSensitivity.Sensitive, UpdatedAtUtc)
        });

        var result = await SettingsEndpoints.GetSettingsAsync(service, CancellationToken.None);

        var okResult = Assert.IsType<Ok<ControllerSettingsResponseDto>>(result);
        var settings = okResult.Value!.Settings;
        Assert.Equal("12", settings.Single(setting => setting.Key == ControllerSettingDefaults.BatteryTotalCapacityKwhKey).Value);
        Assert.Null(settings.Single(setting => setting.Key == ControllerSettingDefaults.TibberAccessTokenKey).Value);
        Assert.True(settings.Single(setting => setting.Key == ControllerSettingDefaults.TibberAccessTokenKey).IsConfigured);
    }

    [Fact]
    public async Task UpdateSettingAsyncReturnsUpdatedSettingDto()
    {
        var service = new FakeControllerSettingsService(Array.Empty<ControllerSetting>());
        var request = new UpdateControllerSettingRequestDto("14");

        var result = await SettingsEndpoints.UpdateSettingAsync(
            ControllerSettingDefaults.BatteryTotalCapacityKwhKey,
            request,
            service,
            CancellationToken.None);

        var okResult = Assert.IsType<Ok<ControllerSettingResponseDto>>(result);
        Assert.Equal(ControllerSettingDefaults.BatteryTotalCapacityKwhKey, okResult.Value!.Key);
        Assert.Equal("14", okResult.Value.Value);
    }

    [Fact]
    public async Task UpdateSettingAsyncReturnsBadRequestForUnknownSetting()
    {
        var service = new FakeControllerSettingsService(Array.Empty<ControllerSetting>())
        {
            UpdateException = new KeyNotFoundException("Die Einstellung 'unknown.setting' ist nicht bekannt.")
        };

        var result = await SettingsEndpoints.UpdateSettingAsync(
            "unknown.setting",
            new UpdateControllerSettingRequestDto("value"),
            service,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<SettingsErrorDto>>(result);
        Assert.Contains("nicht bekannt", badRequest.Value!.Message);
    }

    [Fact]
    public async Task GetStatusAsyncReturnsControllerStatus()
    {
        var service = new FakeControllerSettingsService(Array.Empty<ControllerSetting>());
        var victronMqttRuntimeStatus = new VictronMqttRuntimeStatus();

        var result = await SettingsEndpoints.GetStatusAsync(service, victronMqttRuntimeStatus, CancellationToken.None);

        var okResult = Assert.IsType<Ok<ControllerStatusResponseDto>>(result);
        Assert.Equal("Healthy", okResult.Value!.Status);
        Assert.Equal(ControllerSettingDefaults.GetDefinitions().Count, okResult.Value.KnownSettingsCount);
    }

    private sealed class FakeControllerSettingsService : IControllerSettingsService
    {
        private readonly IReadOnlyList<ControllerSetting> settings;

        public FakeControllerSettingsService(IReadOnlyList<ControllerSetting> settings)
        {
            this.settings = settings;
        }

        public Exception? UpdateException { get; init; }

        public Task<IReadOnlyList<ControllerSetting>> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(settings);
        }

        public Task<ControllerSetting> UpdateSettingAsync(
            string key,
            string? value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (UpdateException is not null)
            {
                throw UpdateException;
            }

            return Task.FromResult(new ControllerSetting(key, value, ControllerSettingSensitivity.Normal, UpdatedAtUtc));
        }

        public Task<ControllerStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new ControllerStatusSnapshot(
                "Healthy",
                KnownSettingsCount: ControllerSettingDefaults.GetDefinitions().Count,
                PersistedSettingsCount: ControllerSettingDefaults.GetDefinitions().Count,
                ConfiguredSensitiveSettingsCount: 1,
                GeneratedAtUtc: UpdatedAtUtc));
        }
    }
}
