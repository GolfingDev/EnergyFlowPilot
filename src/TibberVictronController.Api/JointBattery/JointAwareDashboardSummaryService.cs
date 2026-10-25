using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Dashboard;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

public sealed class JointAwareDashboardSummaryService(IServiceProvider services) : IDashboardSummaryService
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken token = default)
    {
        var summary = await services.GetRequiredService<DashboardSummaryService>().GetSummaryAsync(token);
        if (!await JointBatteryOwnership.BlocksLegacyAsync(services.GetRequiredService<IControllerSettingStore>(), token)) return summary;
        var manual = await services.GetRequiredService<JointManualChargeService>().GetStatusAsync(token);
        return summary with { Overview = JointBatteryDashboardOverview.Create(
            services.GetRequiredService<JointBatteryRuntimeState>().Current.Plan,
            services.GetRequiredService<JointBatteryExecutionState>().Current,
            services.GetRequiredService<IUtcClock>().UtcNow, manual.IsActive) };
    }

    public Task<DashboardReview> GetReviewAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken token = default) =>
        services.GetRequiredService<DashboardSummaryService>().GetReviewAsync(start, end, token);
}
