using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Services;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Registers controller services and keeps Program.cs focused on the application pipeline.
/// </summary>
public static class ControllerServiceCollectionExtensions
{
    private const string ControllerDatabaseConnectionName = "ControllerDatabase";

    /// <summary>
    /// Registers SQLite persistence, repositories and application services used by the API.
    /// </summary>
    public static IServiceCollection AddControllerApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ControllerDatabaseConnectionName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Die SQLite-Connection-String-Einstellung 'ControllerDatabase' fehlt.");
        }

        services.AddSingleton<IUtcClock, SystemUtcClock>();
        services.AddDbContext<ControllerDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ControllerDbInitializer>();
        services.AddScoped<IControllerSettingStore, EfControllerSettingStore>();
        services.AddScoped<IDecisionLogRepository, EfDecisionLogRepository>();
        services.AddScoped<IOperationalEventRepository, EfOperationalEventRepository>();
        services.AddScoped<IControllerSettingsService, ControllerSettingsService>();

        return services;
    }
}
