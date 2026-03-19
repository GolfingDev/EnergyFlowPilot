using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Web.Controllers;
using TibberVictronController.Web.Data;
using TibberVictronController.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.Configure<ControllerOptions>(builder.Configuration.GetSection("Controller"));
builder.Services.Configure<TibberOptions>(builder.Configuration.GetSection("Tibber"));
builder.Services.Configure<VictronOptions>(builder.Configuration.GetSection("Victron"));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHttpClient();

builder.Services.AddSingleton<TibberPriceProvider>();
builder.Services.AddSingleton<VictronMqttClient>();
builder.Services.AddScoped<IDecisionHistoryStore, SqliteDecisionHistoryStore>();
builder.Services.AddScoped<IEnergyStateHistoryStore, SqliteEnergyStateHistoryStore>();
builder.Services.AddScoped<IConsumptionForecastService, ConsumptionForecastService>();
builder.Services.AddScoped<DecisionEngine>();
builder.Services.AddScoped<IDashboardQueryService, DashboardQueryService>();

builder.Services.AddHostedService<EnergyControllerService>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Directory.CreateDirectory(Path.GetDirectoryName(db.Database.GetConnectionString()!.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase))!);
    db.Database.Migrate();
}

DashboardApiController.Map(app);

app.Run();
