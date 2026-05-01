using System.Reflection;

namespace TibberVictronController.Business.Tests;

public sealed class ProjectStructureTests
{
    [Fact]
    public void BusinessProjectCanBeLoaded()
    {
        var assembly = Assembly.Load("TibberVictronController.Business");

        Assert.Equal("TibberVictronController.Business", assembly.GetName().Name);
    }
}
