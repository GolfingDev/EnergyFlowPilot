using TibberVictronController.Business.Auditing;
using TibberVictronController.Business.Decisions;

namespace TibberVictronController.Business.Tests;

public sealed class DecisionAuditArtifactExportTests
{
    [Fact]
    public void WritesGoldenScenarioAuditArtifactsForManualReview()
    {
        var scenario = DecisionAuditGoldenScenarioFactory.Create(initialStateOfChargePercent: 37m);
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());
        var report = harness.Run(scenario);
        var artifactDirectory = GetArtifactDirectory();
        var csvPath = Path.Combine(artifactDirectory.FullName, "golden-scenario.csv");
        var jsonPath = Path.Combine(artifactDirectory.FullName, "golden-scenario.json");

        File.WriteAllText(csvPath, DecisionAuditExporter.ExportCsv(report));
        File.WriteAllText(jsonPath, DecisionAuditExporter.ExportJson(report));

        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(jsonPath));
        Assert.Contains("startsAtUtc", File.ReadAllText(csvPath));
        Assert.Contains("\"decisionSlots\"", File.ReadAllText(jsonPath));
    }

    private static DirectoryInfo GetArtifactDirectory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var artifactDirectory = new DirectoryInfo(Path.Combine(
            repositoryRoot.FullName,
            "artifacts",
            "decision-audit"));

        artifactDirectory.Create();

        return artifactDirectory;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "TibberVictronController.slnx")))
            {
                return currentDirectory;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException("Das Repository-Verzeichnis fuer Audit-Artefakte wurde nicht gefunden.");
    }
}
