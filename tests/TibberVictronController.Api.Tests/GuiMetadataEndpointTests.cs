using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Metadata;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class GuiMetadataEndpointTests
{
    [Fact]
    public void GetGuiMetadataReturnsSettingsAndDecisionRuleDescriptions()
    {
        var result = GuiMetadataEndpoints.GetGuiMetadata();

        var okResult = Assert.IsType<Ok<GuiMetadataResponseDto>>(result);
        var metadata = okResult.Value!;

        Assert.Contains(metadata.Settings, setting => setting.Key == ControllerSettingDefaults.BatteryPlanningMaximumStateOfChargePercentKey && !string.IsNullOrWhiteSpace(setting.DisplayName));
        Assert.Contains(metadata.Settings, setting => setting.Key == ControllerSettingDefaults.TelemetryGridPowerDeadbandWattsKey && setting.Unit == "W");
        Assert.Contains(metadata.Settings, setting => setting.Key == ControllerSettingDefaults.TibberAccessTokenKey && setting.IsSensitive);
        Assert.Contains(metadata.Settings, setting => setting.Key == ControllerSettingDefaults.PvForecastApiKeyKey && setting.IsSensitive);
        Assert.Contains(metadata.DecisionRules, rule => rule.RuleId == BatteryForecastRuleIds.PlanningMaximumGridChargeLimit);
        Assert.Contains(metadata.DecisionRules, rule => rule.RuleId == CurrentBatteryDecisionRuleIds.StaleSiteTelemetry);
        Assert.Contains(metadata.DecisionRules, rule => rule.RuleId == CurrentBatteryDecisionRuleIds.GridPowerDeadband);
    }
}
