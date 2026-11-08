using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Configuration;

/// <summary>Error answer with the setting keys that would fix it; the page offers to enter them (users allowed to change settings).</summary>
public sealed record ApiErrorDto(string Message, IReadOnlyList<string> MissingSettings);

public static class ApiErrors
{
    public static IResult From(Exception exception, int statusCode) =>
        TypedResults.Json(new ApiErrorDto(exception.Message, MissingSettingException.KeysOf(exception)), statusCode: statusCode);
}

/// <summary>
/// A missing setting that no endpoint handled becomes a readable answer with the setting keys (HTTP 409) instead of a
/// bare server error.
/// </summary>
public sealed class MissingSettingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (MissingSettingException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new ApiErrorDto(exception.Message, exception.SettingKeys));
        }
    }
}
