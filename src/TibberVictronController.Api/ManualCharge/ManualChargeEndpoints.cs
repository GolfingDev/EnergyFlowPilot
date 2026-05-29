using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.ManualCharge;

public static class ManualChargeEndpoints
{
    private static readonly DateTimeOffset DisabledUntilUtc = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IEndpointRouteBuilder MapManualChargeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/manual-charge",
            GetManualChargeAsync)
            .WithName("GetManualCharge")
            .WithTags("ManualCharge");

        endpoints.MapPost(
            "/api/manual-charge",
            StartManualChargeAsync)
            .WithName("StartManualCharge")
            .WithTags("ManualCharge");

        endpoints.MapDelete(
            "/api/manual-charge",
            StopManualChargeAsync)
            .WithName("StopManualCharge")
            .WithTags("ManualCharge");

        return endpoints;
    }

    public static async Task<IResult> GetManualChargeAsync(
        [FromServices]
        IControllerSettingStore controllerSettingStore,
        [FromServices]
        ICurrentBatteryDecisionService currentBatteryDecisionService,
        [FromServices]
        IVictronSetpointPublisher victronSetpointPublisher,
        [FromServices]
        IUtcClock utcClock,
        CancellationToken cancellationToken)
    {
        var status = await ReadStatusAsync(controllerSettingStore, utcClock.UtcNow, cancellationToken);

        return TypedResults.Ok(status);
    }

    public static async Task<IResult> StartManualChargeAsync(
        [FromBody]
        ManualChargeRequestDto request,
        [FromServices]
        IControllerSettingStore controllerSettingStore,
        [FromServices]
        ICurrentBatteryDecisionService currentBatteryDecisionService,
        [FromServices]
        IVictronSetpointPublisher victronSetpointPublisher,
        [FromServices]
        IUtcClock utcClock,
        CancellationToken cancellationToken)
    {
        if (request.DurationMinutes is < 1 or > 1440)
        {
            return TypedResults.BadRequest(new ManualChargeErrorDto("Die manuelle Ladedauer muss zwischen 1 und 1440 Minuten liegen."));
        }

        if (request.PowerKw <= 0m || request.PowerKw > 50m)
        {
            return TypedResults.BadRequest(new ManualChargeErrorDto("Die manuelle Ladeleistung muss groesser 0 und maximal 50 kW sein."));
        }

        var powerWatts = (int)Math.Round(request.PowerKw * 1000m, MidpointRounding.AwayFromZero);
        var expiresAtUtc = utcClock.UtcNow.AddMinutes(request.DurationMinutes);

        await SaveManualChargeAsync(controllerSettingStore, powerWatts, expiresAtUtc, utcClock.UtcNow, cancellationToken);
        await TriggerImmediateDecisionAsync(
            controllerSettingStore,
            currentBatteryDecisionService,
            victronSetpointPublisher,
            cancellationToken);

        return TypedResults.Ok(CreateStatus(powerWatts, expiresAtUtc, utcClock.UtcNow));
    }

    public static async Task<IResult> StopManualChargeAsync(
        [FromServices]
        IControllerSettingStore controllerSettingStore,
        [FromServices]
        ICurrentBatteryDecisionService currentBatteryDecisionService,
        [FromServices]
        IVictronSetpointPublisher victronSetpointPublisher,
        [FromServices]
        IUtcClock utcClock,
        CancellationToken cancellationToken)
    {
        await SaveManualChargeAsync(controllerSettingStore, 0, DisabledUntilUtc, utcClock.UtcNow, cancellationToken);
        await TriggerImmediateDecisionAsync(
            controllerSettingStore,
            currentBatteryDecisionService,
            victronSetpointPublisher,
            cancellationToken);

        return TypedResults.Ok(CreateStatus(0, null, utcClock.UtcNow));
    }

    private static async Task TriggerImmediateDecisionAsync(
        IControllerSettingStore controllerSettingStore,
        ICurrentBatteryDecisionService currentBatteryDecisionService,
        IVictronSetpointPublisher victronSetpointPublisher,
        CancellationToken cancellationToken)
    {
        var decisionResult = await currentBatteryDecisionService.CalculateCurrentDecisionAsync(cancellationToken);
        if (await IsDryRunAsync(controllerSettingStore, cancellationToken))
        {
            return;
        }

        await victronSetpointPublisher.PublishAsync(decisionResult, cancellationToken);
    }

    private static async Task<bool> IsDryRunAsync(
        IControllerSettingStore controllerSettingStore,
        CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.VictronDryRunKey,
            cancellationToken);

        return setting is null ||
            !setting.IsConfigured ||
            !bool.TryParse(setting.Value, out var isDryRun) ||
            isDryRun;
    }

    private static async Task<ManualChargeStatusDto> ReadStatusAsync(
        IControllerSettingStore controllerSettingStore,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var powerSetting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.ManualChargePowerWattsKey,
            cancellationToken);
        var expiresAtSetting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.ManualChargeExpiresAtUtcKey,
            cancellationToken);

        var powerWatts = powerSetting?.Value is not null &&
            int.TryParse(powerSetting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPowerWatts)
            ? Math.Max(0, parsedPowerWatts)
            : 0;
        var expiresAtUtc = expiresAtSetting?.Value is not null &&
            DateTimeOffset.TryParse(
                expiresAtSetting.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedExpiresAtUtc) &&
            parsedExpiresAtUtc.Offset == TimeSpan.Zero
            ? parsedExpiresAtUtc
            : (DateTimeOffset?)null;

        return CreateStatus(powerWatts, expiresAtUtc, nowUtc);
    }

    private static async Task SaveManualChargeAsync(
        IControllerSettingStore controllerSettingStore,
        int powerWatts,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await controllerSettingStore.SaveSettingAsync(
            new ControllerSetting(
                ControllerSettingDefaults.ManualChargePowerWattsKey,
                powerWatts.ToString(CultureInfo.InvariantCulture),
                ControllerSettingSensitivity.Normal,
                updatedAtUtc),
            cancellationToken);
        await controllerSettingStore.SaveSettingAsync(
            new ControllerSetting(
                ControllerSettingDefaults.ManualChargeExpiresAtUtcKey,
                expiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ControllerSettingSensitivity.Normal,
                updatedAtUtc),
            cancellationToken);
    }

    private static ManualChargeStatusDto CreateStatus(
        int powerWatts,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset nowUtc)
    {
        var remainingSeconds = expiresAtUtc is null
            ? 0
            : Math.Max(0, (int)Math.Ceiling((expiresAtUtc.Value - nowUtc).TotalSeconds));
        var isActive = powerWatts > 0 && remainingSeconds > 0;

        return new ManualChargeStatusDto(
            isActive,
            isActive ? powerWatts : 0,
            isActive ? powerWatts / 1000m : 0m,
            isActive ? expiresAtUtc : null,
            isActive ? remainingSeconds : 0);
    }
}
