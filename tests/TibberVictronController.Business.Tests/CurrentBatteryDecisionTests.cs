using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class CurrentBatteryDecisionTests
{
    [Theory]
    [InlineData(BatteryDecisionState.Charge, BatteryChargeSource.Grid)]
    [InlineData(BatteryDecisionState.Discharge, null)]
    public void ConstructorRejectsZeroPowerWhenDecisionMovesEnergy(
        BatteryDecisionState decisionState,
        BatteryChargeSource? chargeSource)
    {
        var instruction = new BatteryDecisionInstruction(decisionState, chargeSource);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new CurrentBatteryDecision(instruction, targetPowerWatts: 0));

        Assert.Contains("Beim Laden oder Entladen muss die Ziel-Leistung mehr als 0 Watt betragen.", exception.Message);
    }

    [Fact]
    public void ConstructorRejectsNegativePower()
    {
        var instruction = new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new CurrentBatteryDecision(instruction, targetPowerWatts: -1));

        Assert.Contains("Die Ziel-Leistung darf nicht negativ sein.", exception.Message);
    }

    [Fact]
    public void ConstructorRejectsPowerWhenDecisionIsIdle()
    {
        var instruction = new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null);

        var exception = Assert.Throws<ArgumentException>(
            () => new CurrentBatteryDecision(instruction, targetPowerWatts: 250));

        Assert.Contains("Bei Idle muss die Ziel-Leistung 0 Watt betragen.", exception.Message);
    }

    [Theory]
    [InlineData(BatteryDecisionState.Charge, BatteryChargeSource.PV, 1200)]
    [InlineData(BatteryDecisionState.Discharge, null, 800)]
    [InlineData(BatteryDecisionState.Idle, null, 0)]
    public void ConstructorAcceptsValidPowerForDecisionState(
        BatteryDecisionState decisionState,
        BatteryChargeSource? chargeSource,
        int targetPowerWatts)
    {
        var instruction = new BatteryDecisionInstruction(decisionState, chargeSource);

        var decision = new CurrentBatteryDecision(instruction, targetPowerWatts);

        Assert.Equal(instruction, decision.Instruction);
        Assert.Equal(targetPowerWatts, decision.TargetPowerWatts);
    }
}
