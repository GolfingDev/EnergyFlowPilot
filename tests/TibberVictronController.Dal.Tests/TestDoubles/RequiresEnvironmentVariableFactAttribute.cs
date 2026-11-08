namespace TibberVictronController.Dal.Tests.TestDoubles;

/// <summary>
/// Live test against a real external service. It runs only on explicit request (<c>RUN_LIVE_TESTS=1</c>):
/// a token stored in the user environment alone must not turn the normal test run into calls to production APIs.
/// </summary>
internal sealed class RequiresEnvironmentVariableFactAttribute : FactAttribute
{
    public const string RunLiveTestsEnvironmentVariable = "RUN_LIVE_TESTS";

    public RequiresEnvironmentVariableFactAttribute(string environmentVariableName)
    {
        if (EnvironmentVariableReader.GetValue(RunLiveTestsEnvironmentVariable) != "1")
        {
            Skip = $"Live-Test uebersprungen. Ausfuehren mit {RunLiveTestsEnvironmentVariable}=1 und gesetzter Environment Variable '{environmentVariableName}'.";
        }
        else if (string.IsNullOrWhiteSpace(EnvironmentVariableReader.GetValue(environmentVariableName)))
        {
            Skip = $"Integrationstest uebersprungen, weil die Environment Variable '{environmentVariableName}' nicht gesetzt ist.";
        }
    }
}
