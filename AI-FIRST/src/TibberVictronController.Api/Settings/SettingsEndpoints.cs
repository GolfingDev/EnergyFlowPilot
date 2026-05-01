using Microsoft.AspNetCore.Mvc;
using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Api.Settings;

/// <summary>
/// Maps settings and controller status endpoints without placing business rules in HTTP handlers.
/// </summary>
public static class SettingsEndpoints
{
    /// <summary>
    /// Registers settings and status endpoints used by the Vue frontend.
    /// </summary>
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/settings",
            GetSettingsAsync)
            .WithName("GetControllerSettings")
            .WithTags("Settings");

        endpoints.MapPut(
            "/api/settings/{key}",
            UpdateSettingAsync)
            .WithName("UpdateControllerSetting")
            .WithTags("Settings");

        endpoints.MapGet(
            "/api/status",
            GetStatusAsync)
            .WithName("GetControllerStatus")
            .WithTags("Status");

        return endpoints;
    }

    /// <summary>
    /// Returns all persisted settings as frontend-safe DTOs.
    /// </summary>
    public static async Task<IResult> GetSettingsAsync(
        [FromServices]
        IControllerSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);

        return TypedResults.Ok(SettingsDtoMapper.MapSettings(settings));
    }

    /// <summary>
    /// Updates one known setting and returns the updated frontend-safe DTO.
    /// </summary>
    public static async Task<IResult> UpdateSettingAsync(
        string key,
        [FromBody]
        UpdateControllerSettingRequestDto request,
        [FromServices]
        IControllerSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        try
        {
            var updatedSetting = await settingsService.UpdateSettingAsync(
                key,
                request.Value,
                cancellationToken);

            return TypedResults.Ok(SettingsDtoMapper.MapSetting(updatedSetting));
        }
        catch (ArgumentException exception)
        {
            return TypedResults.BadRequest(new SettingsErrorDto(exception.Message));
        }
        catch (KeyNotFoundException exception)
        {
            return TypedResults.BadRequest(new SettingsErrorDto(exception.Message));
        }
    }

    /// <summary>
    /// Returns the current controller status derived from persisted settings.
    /// </summary>
    public static async Task<IResult> GetStatusAsync(
        [FromServices]
        IControllerSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var status = await settingsService.GetStatusAsync(cancellationToken);

        return TypedResults.Ok(SettingsDtoMapper.MapStatus(status));
    }
}
