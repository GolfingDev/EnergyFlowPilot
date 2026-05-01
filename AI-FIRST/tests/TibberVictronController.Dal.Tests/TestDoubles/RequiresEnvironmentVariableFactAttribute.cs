namespace TibberVictronController.Dal.Tests.TestDoubles;

internal sealed class RequiresEnvironmentVariableFactAttribute : FactAttribute
{
    public RequiresEnvironmentVariableFactAttribute(string environmentVariableName)
    {
        if (string.IsNullOrWhiteSpace(EnvironmentVariableReader.GetValue(environmentVariableName)))
        {
            Skip = $"Integrationstest uebersprungen, weil die Environment Variable '{environmentVariableName}' nicht gesetzt ist.";
        }
    }
}
