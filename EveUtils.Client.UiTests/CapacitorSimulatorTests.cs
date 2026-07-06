using EveUtils.Shared.Modules.Dogma;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The discrete-event capacitor simulator. Validated end-to-end against the reference oracle in the server harness (stable 53.9%,
/// unstable 40.0s); these lock in the outcome branches on small deterministic inputs.
/// </summary>
public class CapacitorSimulatorTests
{
    [Fact]
    public void NoDrains_IsStableAtFull()
    {
        var result = new CapacitorSimulator(1000, 10000).Run([]);
        Assert.True(result.Stable);
        Assert.Equal(100, result.StablePercent);
    }

    [Fact]
    public void DrainBelowPeakRecharge_IsStable()
    {
        // Peak recharge = 2.5 * 1000 / (10000/1000) = 250 GJ/s; a 50 GJ/s drain is well under it.
        var drain = new CapDrain(Duration: 1000, CapNeed: 50, ClipSize: 0, DisableStagger: true, ReloadTime: 0, IsInjector: false);
        var result = new CapacitorSimulator(1000, 10000).Run([drain]);
        Assert.True(result.Stable);
        Assert.True(result.StablePercent > 80, $"expected a high stable level, got {result.StablePercent}");
    }

    [Fact]
    public void DrainAboveRecharge_DepletesInExpectedTime()
    {
        // rechargeRate enormous -> negligible regen. Start 1000, spend 600 at t=0 (->400) then 600 at t=1000 -> below
        // zero, so it runs dry at t=1000ms = 1.0s.
        var drain = new CapDrain(Duration: 1000, CapNeed: 600, ClipSize: 0, DisableStagger: true, ReloadTime: 0, IsInjector: false);
        var result = new CapacitorSimulator(1000, 1_000_000_000).Run([drain]);
        Assert.False(result.Stable);
        Assert.Equal(1.0, result.DepletesInSeconds, 2);
    }
}
