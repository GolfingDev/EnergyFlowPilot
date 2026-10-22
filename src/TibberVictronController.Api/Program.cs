using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.Dashboard;
using TibberVictronController.Api.Decision;
using TibberVictronController.Api.Diagnostics;
using TibberVictronController.Api.Forecast;
using TibberVictronController.Api.HagerEnergy;
using TibberVictronController.Api.Health;
using TibberVictronController.Api.Homematic;
using TibberVictronController.Api.ManualCharge;
using TibberVictronController.Api.Metadata;
using TibberVictronController.Api.Savings;
using TibberVictronController.Api.Settings;
using TibberVictronController.Api.Weishaupt;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Dal.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FileExceptionLogOptions>(builder.Configuration.GetSection("FileExceptionLogging"));
builder.Services.AddControllerApplication(builder.Configuration);

var app = builder.Build();
app.UseFileExceptionLogging();

await InitializeDatabaseAsync(app);

app.MapHealthEndpoints();
app.MapSettingsEndpoints();
app.MapForecastEndpoints();
app.MapCurrentDecisionEndpoints();
app.MapManualChargeEndpoints();
app.MapSavingsEndpoints();
app.MapGuiMetadataEndpoints();
app.MapE3dcEndpoints();
app.MapHomematicEndpoints();
app.MapWeishauptHotWaterEndpoints();
app.MapWeishauptEnergyEndpoints();
app.MapDashboardTelemetryEndpoints();
app.MapDashboardSummaryEndpoints();
app.MapDashboardHub();

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var utcClock = scope.ServiceProvider.GetRequiredService<IUtcClock>();
    var databaseInitializer = scope.ServiceProvider.GetRequiredService<ControllerDbInitializer>();

    await databaseInitializer.InitializeAsync(utcClock.UtcNow);
}

public partial class Program;
