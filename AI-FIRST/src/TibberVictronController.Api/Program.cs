var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "Tibber Victron Controller"
}));

app.Run();

public partial class Program;
