using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TibberVictronController.Api.Diagnostics;

/// <summary>
/// Catches unhandled API exceptions, writes a detailed daily file log, and returns a German error response.
/// </summary>
public sealed class FileExceptionLoggingMiddleware
{
    private static readonly SemaphoreSlim FileWriteLock = new(1, 1);
    private static DateOnly? lastCleanupDateUtc;

    private readonly RequestDelegate next;
    private readonly ILogger<FileExceptionLoggingMiddleware> logger;
    private readonly FileExceptionLogOptions options;

    public FileExceptionLoggingMiddleware(
        RequestDelegate next,
        ILogger<FileExceptionLoggingMiddleware> logger,
        IOptions<FileExceptionLogOptions> options)
    {
        this.next = next;
        this.logger = logger;
        this.options = options.Value;
    }

    public async Task InvokeAsync(HttpContext httpContext)
    {
        try
        {
            await next(httpContext);
        }
        catch (Exception exception)
        {
            var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
            logger.LogError(exception, "Unhandled API exception. TraceId: {TraceId}", traceId);

            await WriteExceptionLogAsync(httpContext, exception, traceId);

            if (httpContext.Response.HasStarted)
            {
                throw;
            }

            await WriteErrorResponseAsync(httpContext, exception, traceId);
        }
    }

    private async Task WriteExceptionLogAsync(HttpContext httpContext, Exception exception, string traceId)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var logDirectory = Path.GetFullPath(options.LogDirectory);
        var logFilePath = Path.Combine(logDirectory, $"api-errors-{utcNow:yyyy-MM-dd}.log");
        var logEntry = CreateLogEntry(httpContext, exception, traceId, utcNow);

        await FileWriteLock.WaitAsync(httpContext.RequestAborted);

        try
        {
            Directory.CreateDirectory(logDirectory);
            await File.AppendAllTextAsync(logFilePath, logEntry, Encoding.UTF8, httpContext.RequestAborted);
            DeleteExpiredLogFiles(logDirectory, utcNow);
        }
        finally
        {
            FileWriteLock.Release();
        }
    }

    private string CreateLogEntry(
        HttpContext httpContext,
        Exception exception,
        string traceId,
        DateTimeOffset utcNow)
    {
        var logEntry = new StringBuilder();

        logEntry.AppendLine("============================================================");
        logEntry.AppendLine($"TimestampUtc: {utcNow:O}");
        logEntry.AppendLine($"TraceId: {traceId}");
        logEntry.AppendLine($"Request: {httpContext.Request.Method} {CreateSafeRequestTarget(httpContext.Request)}");
        logEntry.AppendLine($"RemoteIp: {httpContext.Connection.RemoteIpAddress}");
        logEntry.AppendLine($"ExceptionType: {exception.GetType().FullName}");
        logEntry.AppendLine($"Message: {exception.Message}");
        logEntry.AppendLine("StackTrace:");
        logEntry.AppendLine(exception.ToString());
        logEntry.AppendLine();

        return logEntry.ToString();
    }

    private string CreateSafeRequestTarget(HttpRequest request)
    {
        if (!request.Query.Any())
        {
            return request.Path;
        }

        var queryParts = request.Query.Select(queryItem =>
        {
            var value = IsSensitiveQueryKey(queryItem.Key)
                ? "***"
                : queryItem.Value.ToString();

            return $"{WebUtility.UrlEncode(queryItem.Key)}={WebUtility.UrlEncode(value)}";
        });

        return $"{request.Path}?{string.Join("&", queryParts)}";
    }

    private static bool IsSensitiveQueryKey(string key)
    {
        return key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || key.Contains("api_key", StringComparison.OrdinalIgnoreCase);
    }

    private void DeleteExpiredLogFiles(string logDirectory, DateTimeOffset utcNow)
    {
        var cleanupDateUtc = DateOnly.FromDateTime(utcNow.UtcDateTime);

        if (lastCleanupDateUtc == cleanupDateUtc)
        {
            return;
        }

        lastCleanupDateUtc = cleanupDateUtc;
        var retentionDays = Math.Max(1, options.RetentionDays);
        var deleteBeforeUtc = utcNow.UtcDateTime.Date.AddDays(-retentionDays);

        foreach (var logFile in Directory.EnumerateFiles(logDirectory, "api-errors-*.log"))
        {
            if (File.GetLastWriteTimeUtc(logFile) < deleteBeforeUtc)
            {
                File.Delete(logFile);
            }
        }
    }

    private static async Task WriteErrorResponseAsync(
        HttpContext httpContext,
        Exception exception,
        string traceId)
    {
        var response = new
        {
            message = "Es ist ein unerwarteter API-Fehler aufgetreten. Details wurden im Server-Log gespeichert.",
            exceptionMessage = exception.Message,
            exceptionType = exception.GetType().FullName,
            traceId
        };

        httpContext.Response.Clear();
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        await httpContext.Response.WriteAsync(JsonSerializer.Serialize(response), httpContext.RequestAborted);
    }
}
