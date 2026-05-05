using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryDecisionInstructionTests
{
    [Theory]
    [InlineData(BatteryDecisionState.Discharge)]
    [InlineData(BatteryDecisionState.Idle)]
    public void ConstructorRejectsChargeSourceWhenStateDoesNotCharge(BatteryDecisionState decisionState)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new BatteryDecisionInstruction(decisionState, BatteryChargeSource.Grid));

        Assert.Contains("Eine Ladequelle darf nur bei einer Ladeentscheidung angegeben werden.", exception.Message);
    }

    [Fact]
    public void ConstructorRejectsMissingChargeSourceWhenStateCharges()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new BatteryDecisionInstruction(BatteryDecisionState.Charge, chargeSource: null));

        Assert.Contains("Bei einer Ladeentscheidung muss die Ladequelle angegeben werden.", exception.Message);
    }

    [Theory]
    [InlineData(BatteryChargeSource.Grid)]
    [InlineData(BatteryChargeSource.PV)]
    public void ConstructorAcceptsChargeWithChargeSource(BatteryChargeSource chargeSource)
    {
        var instruction = new BatteryDecisionInstruction(BatteryDecisionState.Charge, chargeSource);

        Assert.Equal(BatteryDecisionState.Charge, instruction.DecisionState);
        Assert.Equal(chargeSource, instruction.ChargeSource);
    }

    [Theory]
    [InlineData(BatteryDecisionState.Discharge)]
    [InlineData(BatteryDecisionState.Idle)]
    public void ConstructorAcceptsNonChargingStateWithoutChargeSource(BatteryDecisionState decisionState)
    {
        var instruction = new BatteryDecisionInstruction(decisionState, chargeSource: null);

        Assert.Equal(decisionState, instruction.DecisionState);
        Assert.Null(instruction.ChargeSource);
    }
}
