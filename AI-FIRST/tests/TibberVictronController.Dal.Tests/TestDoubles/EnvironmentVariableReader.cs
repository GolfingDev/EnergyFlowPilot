namespace TibberVictronController.Dal.Tests.TestDoubles;

internal static class EnvironmentVariableReader
{
    public static string? GetValue(string environmentVariableName)
    {
        return GetConfiguredValue(environmentVariableName, EnvironmentVariableTarget.Process)
            ?? GetConfiguredValue(environmentVariableName, EnvironmentVariableTarget.User)
            ?? GetConfiguredValue(environmentVariableName, EnvironmentVariableTarget.Machine);
    }

    private static string? GetConfiguredValue(
        string environmentVariableName,
        EnvironmentVariableTarget environmentVariableTarget)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariableName, environmentVariableTarget);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
