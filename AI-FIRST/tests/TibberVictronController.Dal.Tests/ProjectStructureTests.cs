using System.Reflection;

namespace TibberVictronController.Dal.Tests;

public sealed class ProjectStructureTests
{
    [Fact]
    public void DalAndBusinessProjectsCanBeLoaded()
    {
        var dalAssembly = Assembly.Load("TibberVictronController.Dal");
        var businessAssembly = Assembly.Load("TibberVictronController.Business");

        Assert.Equal("TibberVictronController.Dal", dalAssembly.GetName().Name);
        Assert.Equal("TibberVictronController.Business", businessAssembly.GetName().Name);
    }
}
