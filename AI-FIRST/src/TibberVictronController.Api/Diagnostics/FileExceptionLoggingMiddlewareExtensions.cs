namespace TibberVictronController.Api.Diagnostics;

/// <summary>
/// Registers file-backed exception logging for the API request pipeline.
/// </summary>
public static class FileExceptionLoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseFileExceptionLogging(this IApplicationBuilder applicationBuilder)
    {
        return applicationBuilder.UseMiddleware<FileExceptionLoggingMiddleware>();
    }
}
