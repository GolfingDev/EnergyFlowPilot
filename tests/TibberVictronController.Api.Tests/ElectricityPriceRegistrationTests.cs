using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Dashboard;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Dal.Prices;

namespace TibberVictronController.Api.Tests;

public sealed class ElectricityPriceRegistrationTests
{
    [Fact]
    public void AllConsumersReceiveTheSameScopedProviderSelection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:ControllerDatabase"] = "Data Source=:memory:" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllerApplication(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var prices = scope.ServiceProvider.GetRequiredService<IElectricityPriceForecastProvider>();
        Assert.IsType<SelectedElectricityPriceForecastProvider>(prices);
        Assert.Same(prices, scope.ServiceProvider.GetRequiredService<CurrentBatteryDecisionServiceDependencies>().TibberPriceForecastProvider);
        Assert.Same(prices, scope.ServiceProvider.GetRequiredService<DashboardSummaryServiceDependencies>().TibberPriceForecastProvider);
    }
}
