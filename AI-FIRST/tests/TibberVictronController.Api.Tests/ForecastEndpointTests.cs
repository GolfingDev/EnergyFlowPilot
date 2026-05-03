using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Forecast;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Weather;

namespace TibberVictronController.Api.Tests;

public sealed class ForecastEndpointTests
{
    private static readonly DateTimeOffset StartsAtUtc = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetForecastAsyncReturnsForecastDto()
    {
        var forecastService = new FakeBatteryForecastService(CreateForecastResult());

        var result = await ForecastEndpoints.GetForecastAsync(
            StartsAtUtc,
            hours: 1,
            forecastService,
            CancellationToken.None);

        var okResult = Assert.IsType<Ok<BatteryForecastResponseDto>>(result);
        Assert.Single(okResult.Value!.Entries);
    }

    [Fact]
    public async Task GetForecastAsyncRejectsNonUtcStart()
    {
        var forecastService = new FakeBatteryForecastService(CreateForecastResult());
        var localStart = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var result = await ForecastEndpoints.GetForecastAsync(
            localStart,
            hours: 1,
            forecastService,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<ForecastErrorDto>>(result);
        Assert.Contains("UTC", badRequest.Value!.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(73)]
    public async Task GetForecastAsyncRejectsInvalidForecastHours(int hours)
    {
        var forecastService = new FakeBatteryForecastService(CreateForecastResult());

        var result = await ForecastEndpoints.GetForecastAsync(
            StartsAtUtc,
            hours,
            forecastService,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<ForecastErrorDto>>(result);
        Assert.Contains("zwischen 1 und 72", badRequest.Value!.Message);
    }

    [Fact]
    public async Task GetForecastAsyncReturnsBadRequestWhenForecastSolarFails()
    {
        var forecastService = new ThrowingBatteryForecastService(
            new ForecastSolarApiException("Forecast.Solar hat den Request mit HTTP 429 beantwortet."));

        var result = await ForecastEndpoints.GetForecastAsync(
            StartsAtUtc,
            hours: 24,
            forecastService,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<ForecastErrorDto>>(result);
        Assert.Contains("Forecast.Solar", badRequest.Value!.Message);
    }

    [Fact]
    public async Task GetForecastAsyncReturnsBadRequestWhenForecastConfigurationIsInvalid()
    {
        var forecastService = new ThrowingBatteryForecastService(
            new InvalidOperationException("Die Einspeiseverguetung ist nicht konfiguriert."));

        var result = await ForecastEndpoints.GetForecastAsync(
            StartsAtUtc,
            hours: 24,
            forecastService,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<ForecastErrorDto>>(result);
        Assert.Contains("nicht konfiguriert", badRequest.Value!.Message);
    }

    private static BatteryForecastResult CreateForecastResult()
    {
        var timeSlot = new ForecastTimeSlot(StartsAtUtc, StartsAtUtc.AddMinutes(15));
        var decision = new CurrentBatteryDecision(
            new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
            targetPowerWatts: 0);
        var entry = new BatteryForecastEntry(
            timeSlot,
            TibberPricePerKwh: 0.25m,
            TibberPriceCurrency: "EUR",
            ExpectedPvYieldKwh: 0m,
            ExpectedConsumptionKwh: 0.2m,
            ExpectedGridImportBeforeBatteryKwh: 0.2m,
            StateOfChargeBeforePercent: 50m,
            StateOfChargeAfterPercent: 50m,
            decision,
            new[] { new BatteryDecisionReason("TestRule", "Testbegruendung") });

        return new BatteryForecastResult(
            new BatteryState(50m, StartsAtUtc),
            new BatteryConfiguration(10m),
            new[] { entry });
    }

    private sealed class FakeBatteryForecastService : IBatteryForecastService
    {
        private readonly BatteryForecastResult forecastResult;

        public FakeBatteryForecastService(BatteryForecastResult forecastResult)
        {
            this.forecastResult = forecastResult;
        }

        public Task<BatteryForecastResult> CalculateForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(forecastResult);
        }
    }

    private sealed class ThrowingBatteryForecastService : IBatteryForecastService
    {
        private readonly Exception exception;

        public ThrowingBatteryForecastService(Exception exception)
        {
            this.exception = exception;
        }

        public Task<BatteryForecastResult> CalculateForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
    }
}
