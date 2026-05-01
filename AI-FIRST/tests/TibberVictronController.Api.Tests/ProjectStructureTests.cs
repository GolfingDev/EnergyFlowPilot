namespace TibberVictronController.Api.Tests;

public sealed class ProjectStructureTests
{
    [Fact]
    public void ApiProjectCanBeLoaded()
    {
        Assert.Equal("TibberVictronController.Api", typeof(Program).Assembly.GetName().Name);
    }
}
