using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.Decision;
using TibberVictronController.Api.Forecast;
using TibberVictronController.Api.Metadata;
using TibberVictronController.Api.Savings;
using TibberVictronController.Api.Settings;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Dal.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllerApplication(builder.Configuration);

var app = builder.Build();

await InitializeDatabaseAsync(app);

app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "Tibber Victron Controller"
}));

app.MapSettingsEndpoints();
app.MapForecastEndpoints();
app.MapCurrentDecisionEndpoints();
app.MapSavingsEndpoints();
app.MapGuiMetadataEndpoints();

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var utcClock = scope.ServiceProvider.GetRequiredService<IUtcClock>();
    var databaseInitializer = scope.ServiceProvider.GetRequiredService<ControllerDbInitializer>();

    await databaseInitializer.InitializeAsync(utcClock.UtcNow);
}

public partial class Program;
