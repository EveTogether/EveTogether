using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Dogma;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// — remote-assistance model correctness. Tests the record/model layer that the engine populates:
/// <see cref="ModuleContribution"/>, <see cref="DerivedStats"/> and <see cref="FitStats"/>. These do not require the
/// full Dogma engine, which needs an SDE database. The gate logic (maxRange&gt;0 for remote, maxRange==0 for local)
/// is exercised via the <see cref="ModuleContributionKind"/> and the <see cref="DerivedStats.HasRemoteAssistance"/>
/// predicate so that a regression in the separation is visible without hitting real dogma data.
/// </summary>
public class RemoteAssistanceModelTests
{
    // ── ModuleContribution ────────────────────────────────────────────────────

    [Fact]
    public void RemoteRepair_Contribution_Carries_RepPerSec_Layer_And_Range()
    {
        var contrib = new ModuleContribution(
            TypeId: 1001,
            Kind: ModuleContributionKind.RemoteRepair,
            State: ModuleState.Active,
            RepPerSec: 21.3,
            RepairLayer: RepairLayer.Armor,
            RemoteRangeMeters: 8400);

        Assert.Equal(ModuleContributionKind.RemoteRepair, contrib.Kind);
        Assert.Equal(RepairLayer.Armor, contrib.RepairLayer);
        Assert.Equal(21.3, contrib.RepPerSec);
        Assert.Equal(8400, contrib.RemoteRangeMeters);
    }

    [Fact]
    public void RemoteCapTransfer_Contribution_Carries_CapPerSec_And_Range()
    {
        var contrib = new ModuleContribution(
            TypeId: 2001,
            Kind: ModuleContributionKind.RemoteCapTransfer,
            State: ModuleState.Active,
            CapPerSec: 13.0,
            RemoteRangeMeters: 6000);

        Assert.Equal(ModuleContributionKind.RemoteCapTransfer, contrib.Kind);
        Assert.Equal(13.0, contrib.CapPerSec);
        Assert.Equal(6000, contrib.RemoteRangeMeters);
    }

    [Fact]
    public void LocalRepair_Contribution_Has_Zero_RemoteRange()
    {
        // A local repairer must never carry a projection range (gate: maxRange == 0 on the module).
        var contrib = new ModuleContribution(
            TypeId: 3001,
            Kind: ModuleContributionKind.LocalRepair,
            State: ModuleState.Active,
            RepPerSec: 40.0,
            RepairLayer: RepairLayer.Shield);

        Assert.Equal(0, contrib.RemoteRangeMeters);
    }

    // ── DerivedStats.HasRemoteAssistance ──────────────────────────────────────

    [Fact]
    public void DerivedStats_HasRemoteAssistance_False_When_AllZero()
    {
        var stats = _EmptyDerived();
        Assert.False(stats.HasRemoteAssistance);
    }

    [Fact]
    public void DerivedStats_HasRemoteAssistance_True_When_ArmorRepPresent()
    {
        var stats = _EmptyDerived() with { RemoteArmorRepPerSec = 21.3 };
        Assert.True(stats.HasRemoteAssistance);
    }

    [Fact]
    public void DerivedStats_HasRemoteAssistance_True_When_CapTransferPresent()
    {
        var stats = _EmptyDerived() with { RemoteCapPerSec = 13.0 };
        Assert.True(stats.HasRemoteAssistance);
    }

    // ── FitStats.HasRemoteAssistance ──────────────────────────────────────────

    [Fact]
    public void FitStats_HasRemoteAssistance_False_When_AllZero()
    {
        var stats = _EmptyFitStats();
        Assert.False(stats.HasRemoteAssistance);
    }

    [Fact]
    public void FitStats_HasRemoteAssistance_True_When_ShieldRepPresent()
    {
        var stats = _EmptyFitStats() with { RemoteShieldRepPerSec = 28.3 };
        Assert.True(stats.HasRemoteAssistance);
    }

    [Fact]
    public void FitStats_HasRemoteAssistance_True_When_HullRepPresent()
    {
        var stats = _EmptyFitStats() with { RemoteHullRepPerSec = 76.7 };
        Assert.True(stats.HasRemoteAssistance);
    }

    // ── Hand-calculation spot-check (Small Remote Armor Repairer II, ref plan §2) ──
    // Module: armorDamageAmount(84)=64 HP, duration(73)=3000 ms, maxRange(54)=8400 m.
    // Expected: rep/s = 64 / (3000 / 1000) = 21.33… HP/s.  Range = 8.4 km.

    [Fact]
    public void RemoteRepair_RepPerSec_Formula_Spot_Check()
    {
        const double amount = 64.0;
        const double durationMs = 3000.0;
        const double expectedRepPerSec = amount / (durationMs / 1000.0);   // 21.333…

        var contrib = new ModuleContribution(
            TypeId: 33472,   // Small Remote Armor Repairer II (SDE-verified, plan §2)
            Kind: ModuleContributionKind.RemoteRepair,
            State: ModuleState.Active,
            RepPerSec: expectedRepPerSec,
            RepairLayer: RepairLayer.Armor,
            RemoteRangeMeters: 8400);

        Assert.Equal(expectedRepPerSec, contrib.RepPerSec, precision: 3);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DerivedStats _EmptyDerived() => new(
        CpuUsed: 0, CpuOutput: 100, PowerUsed: 0, PowerOutput: 100,
        MaxVelocity: 200, SignatureRadius: 50, AlignTime: 4,
        ShieldEhp: 1000, ArmorEhp: 1000, StructureEhp: 1000, Ehp: 3000,
        TurretDps: 0, DroneDps: 0, MissileDps: 0,
        CapacitorCapacity: 500, CapacitorRecharge: 10, CapacitorUsed: 5,
        CapacitorStable: true, CapacitorStablePercent: 50, CapacitorDepletesInSeconds: 0,
        DroneBandwidthUsed: 0, DroneActiveCount: 0, MiningYield: 0);

    private static FitStats _EmptyFitStats() => new(
        TotalDps: 0, WeaponDps: 0, DroneDps: 0,
        CpuUsed: 0, CpuOutput: 100, PowerUsed: 0, PowerOutput: 100,
        DroneBayUsed: 0, DroneBayAvailable: 0,
        DroneBandwidthUsed: 0, DroneBandwidthAvailable: 0,
        CalibrationUsed: 0, CalibrationAvailable: 400,
        Ehp: 3000, ShieldEhp: 1000, ArmorEhp: 1000, StructureEhp: 1000,
        ShieldResists: new ResistLayer(0, 0, 0, 0),
        ArmorResists: new ResistLayer(0, 0, 0, 0),
        StructureResists: new ResistLayer(0, 0, 0, 0),
        CapacitorStable: true, CapacitorStablePercent: 50,
        CapacitorDepletesInSeconds: 0, CapacitorCapacity: 500,
        CapacitorDelta: 5, CapacitorRecharge: 10,
        TargetingRange: 50000, ScanResolution: 200, MaxLockedTargets: 5, SensorStrength: 12,
        MaxVelocity: 200, Mass: 1200000, Agility: 2.5, AlignTime: 4, WarpSpeed: 3, SignatureRadius: 50,
        ActiveDroneCount: 0, MiningYield: 0,
        ModuleContributions: []);
}
