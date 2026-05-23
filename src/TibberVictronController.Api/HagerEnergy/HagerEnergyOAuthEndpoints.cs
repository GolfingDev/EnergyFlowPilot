using TibberVictronController.Dal.HagerEnergy;

namespace TibberVictronController.Api.HagerEnergy;

/// <summary>
/// Maps Hager Energy OAuth endpoints used by the settings UI.
/// </summary>
public static class HagerEnergyOAuthEndpoints
{
    public static IEndpointRouteBuilder MapHagerEnergyOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/hager-energy/oauth/authorize-url",
            GetAuthorizationUrlAsync)
            .WithName("GetHagerEnergyAuthorizationUrl")
            .WithTags("HagerEnergy");

        endpoints.MapGet(
            "/api/hager-energy/oauth/callback",
            HandleCallbackAsync)
            .WithName("HandleHagerEnergyOAuthCallback")
            .WithTags("HagerEnergy");

        return endpoints;
    }

    public static async Task<IResult> GetAuthorizationUrlAsync(
        HagerEnergyApiClient hagerEnergyApiClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var authorizationUri = await hagerEnergyApiClient.CreateAuthorizationUriAsync(cancellationToken);

            return TypedResults.Ok(new HagerEnergyAuthorizationUrlResponseDto(authorizationUri.ToString()));
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.BadRequest(new HagerEnergyOAuthErrorDto(exception.Message));
        }
    }

    public static async Task<IResult> HandleCallbackAsync(
        string? code,
        string? state,
        string? error,
        HagerEnergyApiClient hagerEnergyApiClient,
        DatabaseHagerEnergySettingsProvider settingsProvider,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return await RedirectToSettingsAsync(settingsProvider, $"error={Uri.EscapeDataString(error)}", cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return await RedirectToSettingsAsync(settingsProvider, "error=missing_code_or_state", cancellationToken);
        }

        try
        {
            await hagerEnergyApiClient.ExchangeAuthorizationCodeAsync(code, state, cancellationToken);

            return await RedirectToSettingsAsync(settingsProvider, "success=1", cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HagerEnergyApiException or ArgumentException)
        {
            return await RedirectToSettingsAsync(settingsProvider, $"error={Uri.EscapeDataString(exception.Message)}", cancellationToken);
        }
    }

    private static async Task<IResult> RedirectToSettingsAsync(
        DatabaseHagerEnergySettingsProvider settingsProvider,
        string query,
        CancellationToken cancellationToken)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var separator = settings.PostLoginRedirectUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        return TypedResults.Redirect($"{settings.PostLoginRedirectUrl}{separator}hagerEnergyAuth={query}");
    }
}
