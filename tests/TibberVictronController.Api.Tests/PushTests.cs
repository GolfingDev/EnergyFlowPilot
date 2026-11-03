using Microsoft.AspNetCore.DataProtection;
using TibberVictronController.Api.Push;

namespace TibberVictronController.Api.Tests;

public sealed class PushTests
{
    [Fact]
    public void AnswerTokenNamesSessionAndUserAndCannotBeForged()
    {
        var tokens = new BaseChoiceAnswerToken(new EphemeralDataProtectionProvider());

        var token = tokens.Create(42, "Torsten");

        Assert.Equal((42, "Torsten"), tokens.Read(token));
        Assert.Null(tokens.Read(token[..^4] + "AAAA"));
        Assert.Null(tokens.Read("42|Torsten"));
        Assert.Null(tokens.Read(null));
        // Another key ring (another installation) cannot read it.
        Assert.Null(new BaseChoiceAnswerToken(new EphemeralDataProtectionProvider()).Read(token));
    }
}
