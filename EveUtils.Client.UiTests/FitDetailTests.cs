using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Imaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Skills;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Skills.Repositories;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Implants.Repositories;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Fit-detail: the EsiFitting → Dogma input mapping pairs charges with their module, groups drones and
/// gives each module its default state; the radial window formats the computed stats, lays out the slot wheel and lets
/// the user cycle a module's state to recompute the fit live. The stats numbers themselves are validated by the
/// engine's own harness (DogmaEftCheck); here we cover the mapping, the window and the recompute plumbing.
/// </summary>
public class FitDetailTests
{
    private static EsiFitting Fit(string name, int shipTypeId, params (int TypeId, string Flag, int Qty)[] items) =>
        new(0, name, "", shipTypeId, items.Select(i => new EsiFittingItem(i.TypeId, i.Flag, i.Qty)).ToList());

    // A stats provider stub: computes from the module states the window passes in, so a recompute is observable.
    private sealed class StubStatsProvider(System.Func<IReadOnlyList<ModuleInput>, FitStats?> compute) : IFitStatsProvider
    {
        public int? LastTacticalModeTypeId { get; private set; }

        public Task<FitStats?> ComputeAsync(EsiFitting fit, CancellationToken cancellationToken = default) =>
            Task.FromResult(compute([]));

        public Task<FitStats?> ComputeAsync(EsiFitting fit, IReadOnlyList<ModuleInput> modules,
            int? tacticalModeTypeId = null, IReadOnlyList<DroneInput>? activeDrones = null,
            IReadOnlyList<ImplantInput>? boosters = null, SkillSource? skills = null,
            DamageProfile? profile = null, WeatherInput? weather = null,
            IReadOnlyList<FighterInput>? activeFighters = null, CancellationToken cancellationToken = default)
        {
            LastTacticalModeTypeId = tacticalModeTypeId;
            LastBoosters = boosters;
            LastSkills = skills;
            LastWeather = weather;
            return Task.FromResult(compute(modules));
        }

        public IReadOnlyList<ImplantInput>? LastBoosters { get; private set; }
        public SkillSource? LastSkills { get; private set; }
        public WeatherInput? LastWeather { get; private set; }
    }

    [Fact]
    public void ModuleState_OnlineEffectAlone_DefaultsToOnline_NotActive()
    {
        // Effect 16 (online) is Active-category but sits on every onlineable module; a module carrying only it (a damage
        // mod / missile guidance enhancer) is passive -> defaults online and offers no Active state. A module that also
        // carries a real active-category effect (an afterburner) defaults to active.
        var data = new FakeDogmaDataAccessor()
            .Type(35771, 296, 7).TypeEffect(35771, 16)                        // enhancer: only the online effect -> passive
            .Type(438, 46, 7).TypeEffect(438, 16).TypeEffect(438, 6731)       // afterburner: online + Active-category effect
            .Type(13001, 87, 7).TypeEffect(13001, 16).TypeEffect(13001, 6197) // nosferatu: online + Target-category effect
            .Effect(16, 1).Effect(6731, 1).Effect(6197, 2);

        Assert.Equal(ModuleState.Online, ModuleStateResolver.DefaultState(35771, data, new ModuleStateAccumulator()));
        Assert.DoesNotContain(ModuleState.Active, ModuleStateResolver.ValidStates(35771, data));
        Assert.Equal(ModuleState.Active, ModuleStateResolver.DefaultState(438, data, new ModuleStateAccumulator()));
        Assert.Contains(ModuleState.Active, ModuleStateResolver.ValidStates(438, data));
        Assert.Equal(ModuleState.Active, ModuleStateResolver.DefaultState(13001, data, new ModuleStateAccumulator()));   // Target-category (nos) is activatable
    }

    [Fact]
    public void ModuleState_OnlineOnlyEffectByName_DefaultsToOnline_EvenThoughActivatable()
    {
        // A cloak carries the cloaking effect (607, category 1 = activatable) yet must default to online for a static
        // readout (the active-state clamp). The clamp is keyed on the SDE effect *name*, so it holds regardless of the
        // effect id. The cloak can still be switched to active manually (it stays in ValidStates).
        var data = new FakeDogmaDataAccessor()
            .Type(11578, 330, 7).TypeEffect(11578, 16).TypeEffect(11578, 607)
            .Effect(16, 1).EffectNamed(607, "cloaking", 1);

        Assert.Equal(ModuleState.Online, ModuleStateResolver.DefaultState(11578, data, new ModuleStateAccumulator()));
        Assert.Contains(ModuleState.Active, ModuleStateResolver.ValidStates(11578, data));
    }

    [Fact]
    public void ModuleState_ActivationBlocked_DefaultsOnline_AndOffersNoActive()
    {
        // attr 2363 activationBlocked > 0: the module has an active effect but is barred from activating (state
        // validation). Without the clamp it would default to active and offer Active — proves red-without-fix.
        var blocked = new FakeDogmaDataAccessor()
            .Type(900, 60, 7, new SdeDogmaAttribute(ModuleStateResolver.ActivationBlockedAttribute, 1))
            .TypeEffect(900, 16).TypeEffect(900, 6731).Effect(16, 1).Effect(6731, 1);
        var allowed = new FakeDogmaDataAccessor()
            .Type(900, 60, 7).TypeEffect(900, 16).TypeEffect(900, 6731).Effect(16, 1).Effect(6731, 1);

        Assert.Equal(ModuleState.Online, ModuleStateResolver.DefaultState(900, blocked, new ModuleStateAccumulator()));
        Assert.DoesNotContain(ModuleState.Active, ModuleStateResolver.ValidStates(900, blocked));
        Assert.Equal(ModuleState.Active, ModuleStateResolver.DefaultState(900, allowed, new ModuleStateAccumulator()));
    }

    [Fact]
    public void ModuleState_MaxGroupOnline_DropsSurplusToOffline()
    {
        // attr 764 maxGroupOnline = 1: only one module of the group may sit online-or-higher; the second drops to
        // offline. The shared accumulator tracks the group across the fit's modules. Type 950 is passive (no active
        // effect) so it resolves to online, exercising the online cap rather than the active cap.
        var data = new FakeDogmaDataAccessor()
            .Type(950, 770, 7, new SdeDogmaAttribute(ModuleStateResolver.MaxGroupOnlineAttribute, 1))
            .TypeEffect(950, 16).Effect(16, 1);
        var accumulator = new ModuleStateAccumulator();

        Assert.Equal(ModuleState.Online, ModuleStateResolver.DefaultState(950, data, accumulator));
        Assert.Equal(ModuleState.Passive, ModuleStateResolver.DefaultState(950, data, accumulator));
    }

    [Fact]
    public void CloakActivation_MutuallyExclusiveWithActiveModule_BothDirections()
    {
        // Cloak (cloaking effect 607, category 1) vs an active mining scoop (miningClouds 2726). Cloak detection is by
        // effect name + category, not type id. The 5s in-game grace has no axis in a static fit -> mutually exclusive.
        var data = new FakeDogmaDataAccessor()
            .Type(11578, 330, 7).TypeEffect(11578, 607).EffectNamed(607, "cloaking", 1)
            .Type(25266, 737, 7).TypeEffect(25266, 2726).EffectNamed(2726, "miningClouds", 2);

        // Activating the cloak while the scoop is active is refused, blocked by the scoop.
        var cloakVsActiveScoop = ModuleActivationRules.FirstConflict(11578, [new ModuleInput(25266, ModuleState.Active)], data);
        Assert.NotNull(cloakVsActiveScoop);
        Assert.Equal(25266, cloakVsActiveScoop!.BlockingTypeId);
        Assert.Equal(ModuleActivationReason.CloakMutualExclusion, cloakVsActiveScoop.Reason);

        // The reverse: activating the scoop while the cloak is active is refused, blocked by the cloak.
        var scoopVsActiveCloak = ModuleActivationRules.FirstConflict(25266, [new ModuleInput(11578, ModuleState.Active)], data);
        Assert.Equal(11578, scoopVsActiveCloak!.BlockingTypeId);

        // No conflict when the other module is merely online (not active), nor between two non-cloak active modules.
        Assert.Null(ModuleActivationRules.FirstConflict(11578, [new ModuleInput(25266, ModuleState.Online)], data));
        Assert.Null(ModuleActivationRules.FirstConflict(25266, [new ModuleInput(2889, ModuleState.Active)], data));
    }

    [Fact]
    public async Task CloakSlot_CycleToActive_RefusedWhileScoopActive_ThenAllowedOnceScoopOff()
    {
        // The fit-detail wheel wires the cloak rule into CycleState: activating the cloak while the scoop is active is
        // refused (state unchanged) with a reason toast; once the scoop is offline the cloak activates with no new toast.
        var sde = new FakeSdeAccessor()
            .Add(587, "Rifter", 25, 6)
            .Add(11578, "Covert Ops Cloaking Device II", 330, 7, SdeSlotType.High)
            .Add(25266, "Gas Cloud Scoop I", 737, 7, SdeSlotType.High);
        var data = new FakeDogmaDataAccessor()
            .Type(11578, 330, 7).TypeEffect(11578, 16).TypeEffect(11578, 607).Effect(16, 1).EffectNamed(607, "cloaking", 1)
            .Type(25266, 737, 7).TypeEffect(25266, 2726).EffectNamed(2726, "miningClouds", 2);
        var toasts = new RecordingToastService();
        var fit = Fit("Cloaky", 587, (11578, "HiSlot0", 1), (25266, "HiSlot1", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde, data, toasts: toasts);
        await vm.InitializeAsync();

        var cloak = vm.RadialSlots.Single(slot => slot.TypeId == 11578);
        var scoop = vm.RadialSlots.Single(slot => slot.TypeId == 25266);
        Assert.Equal(ModuleState.Online, cloak.State);    // online-only cloaking effect -> defaults online
        Assert.Equal(ModuleState.Active, scoop.State);    // active mining effect -> defaults active

        await cloak.CycleStateCommand.ExecuteAsync(null);
        Assert.Equal(ModuleState.Online, cloak.State);    // refused: stays online
        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Contains(FallbackNameResolver.Instance.TypeName(25266), toast.Message!);

        await scoop.CycleStateCommand.ExecuteAsync(null);   // active -> offline
        Assert.Equal(ModuleState.Passive, scoop.State);
        await cloak.CycleStateCommand.ExecuteAsync(null);   // now allowed
        Assert.Equal(ModuleState.Active, cloak.State);
        Assert.Single(toasts.Toasts);                       // no new toast
    }

    [Fact]
    public async Task OffenseTotal_ReadsAsSpoolRange_WithTurretMissileDroneBreakdown()
    {
        // A disintegrator fit (TotalDpsMax > TotalDps) shows the OFFENSE total as a base–max range; the breakdown tooltip
        // splits turrets (with their spool range) / missiles / drones.
        var stats = SampleStats() with
        {
            TotalDps = 500, TotalDpsMax = 1350, TurretDps = 400, TurretDpsMax = 1250, MissileDps = 0, DroneDps = 100
        };
        var vm = new FitDetailWindowViewModel(Fit("Drekavac", 52254), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => stats), sde: null, data: null);
        await vm.InitializeAsync();

        Assert.Equal("500.0 – 1350.0 dps", vm.TotalDpsLabel);
        Assert.Contains("Turrets: 400.0 – 1250.0 dps", vm.DpsBreakdown);
        Assert.Contains("Drones: 100.0 dps", vm.DpsBreakdown);
        Assert.DoesNotContain("Missiles", vm.DpsBreakdown!);
    }

    [Fact]
    public async Task OffenseTotal_ReadsAsSingleValue_WhenNoSpoolWeaponFitted()
    {
        var stats = SampleStats() with
        {
            TotalDps = 491.5, TotalDpsMax = 491.5, TurretDps = 356.7, TurretDpsMax = 356.7, DroneDps = 134.8
        };
        var vm = new FitDetailWindowViewModel(Fit("Thorax", 627), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => stats), sde: null, data: null);
        await vm.InitializeAsync();

        Assert.Equal("491.5 dps", vm.TotalDpsLabel);   // no range without a spooling weapon
    }

    [Fact]
    public async Task DroneBayTooltip_IncludesPerDroneReadout_FromItsContribution()
    {
        // Each deployed drone stack is handed its own per-drone contribution, so its bay tooltip reads out the in-game
        // DPS / range / tracking next to the bandwidth line.
        var stats = SampleStats() with
        {
            ModuleContributions = [new ModuleContribution(8, ModuleContributionKind.Drone, ModuleState.Active,
                IsDrone: true, Dps: 33.0, OptimalRange: 12000, TrackingSpeed: 1.2)]
        };
        var vm = new FitDetailWindowViewModel(Fit("Drone Thorax", 627, (8, "DroneBay", 5)), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => stats), sde: null, data: null);
        await vm.InitializeAsync();

        var tooltip = Assert.Single(vm.DroneBay).Tooltip;
        Assert.Contains("Damage Per Second 33.0", tooltip);
        Assert.Contains("Optimal 12.0 km", tooltip);
        Assert.Contains("Bandwidth Needed", tooltip);
    }

    [Fact]
    public async Task OffenseBreakdown_ShowsReloadSustained_ForClippedWeapons_NotForDrones()
    {
        var stats = SampleStats() with
        {
            TotalDps = 500, TurretDps = 400, TurretDpsSustained = 360, MissileDps = 0, DroneDps = 100
        };
        var vm = new FitDetailWindowViewModel(Fit("Vexor", 626), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => stats), sde: null, data: null);
        await vm.InitializeAsync();

        Assert.Contains("Turrets: 400.0 dps (reload 360.0)", vm.DpsBreakdown);
        Assert.Contains("Drones: 100.0 dps", vm.DpsBreakdown!);
        Assert.DoesNotContain("reload", vm.DpsBreakdown!.Split('\n').Last());   // the drone line carries no reload note
    }

    [Fact]
    public void Mapper_PairsChargeWithModule_GroupsDrones_AndSkipsCargo()
    {
        var sde = FakeSdeAccessor.WithSampleFit();
        var data = new FakeDogmaDataAccessor()
            .Type(2889, 74, 7).TypeEffect(2889, 10).Effect(10, 1)   // AutoCannon: has an active effect
            .Type(438, 46, 7).TypeEffect(438, 11).Effect(11, 1)     // Afterburner: has an active effect
            .Type(2048, 60, 7);                                     // Damage Control: no active effect -> online
        // AutoCannon (High, turret 2889) + EMP S charge (12608) in the same slot; AB (438) mid; DC (2048) low;
        // 3 Hobgoblin II drones (2456); Nanite Paste in cargo (28668) must be ignored.
        var fit = Fit("Rifter", 587,
            (2889, "HiSlot0", 1), (12608, "HiSlot0", 1),
            (438, "MedSlot0", 1),
            (2048, "LoSlot0", 1),
            (2456, "DroneBay", 3),
            (28668, "Cargo", 10));

        var modules = FitInputMapper.BuildModules(fit, sde, data);
        var drones = FitInputMapper.BuildDrones(fit);

        Assert.Equal(3, modules.Count); // turret + AB + DC; cargo + charge are not modules
        var turret = modules.Single(m => m.TypeId == 2889);
        Assert.Equal(12608, turret.ChargeTypeId);       // charge paired into the turret's slot
        Assert.Equal(ModuleState.Active, turret.State);                                 // turret has an active effect -> active
        Assert.Equal(ModuleState.Active, modules.Single(m => m.TypeId == 438).State);   // afterburner is active
        Assert.Equal(ModuleState.Online, modules.Single(m => m.TypeId == 2048).State);  // damage control: no active effect -> online

        var drone = Assert.Single(drones);
        Assert.Equal(2456, drone.TypeId);
        Assert.Equal(3, drone.Amount);                  // grouped quantity
    }

    private static FitStats SampleStats(double cpuUsed = 358.8) => new(
        TotalDps: 491.5, WeaponDps: 356.7, DroneDps: 134.8,
        CpuUsed: cpuUsed, CpuOutput: 412.5, PowerUsed: 1064.6, PowerOutput: 1075.0,
        DroneBayUsed: 50, DroneBayAvailable: 50, DroneBandwidthUsed: 50, DroneBandwidthAvailable: 50,
        CalibrationUsed: 0, CalibrationAvailable: 400,
        Ehp: 27156, ShieldEhp: 18700, ArmorEhp: 3490, StructureEhp: 4980,
        ShieldResists: new ResistLayer(39, 30, 48, 56),
        ArmorResists: new ResistLayer(57, 45, 45, 24),
        StructureResists: new ResistLayer(60, 60, 60, 60),
        CapacitorStable: true, CapacitorStablePercent: 14.1, CapacitorDepletesInSeconds: 0,
        CapacitorCapacity: 1740, CapacitorDelta: 1.6, CapacitorRecharge: 4.5,
        TargetingRange: 63000, ScanResolution: 336, MaxLockedTargets: 6, SensorStrength: 18,
        MaxVelocity: 697, Mass: 16280000, Agility: 0.3528, AlignTime: 7.96, WarpSpeed: 4.0, SignatureRadius: 130,
        ActiveDroneCount: 5, MiningYield: 0, ModuleContributions: []);

    [Fact]
    public async Task DetailWindowVm_FormatsStats_AndLaysOutSlotsAndDrones()
    {
        var fit = Fit("Thorax PVE", 627,
            (2, "HiSlot0", 1), (2, "HiSlot1", 1),
            (4, "MedSlot0", 1),
            (5, "LoSlot0", 1),
            (8, "DroneBay", 5));

        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: null);
        await vm.InitializeAsync();

        Assert.True(vm.HasStats);
        Assert.Equal(4, vm.RadialSlots.Count);   // 2 high + 1 mid + 1 low (drones are not on the ring)
        Assert.Single(vm.DroneBay);
        Assert.Equal("491.5 dps", vm.TotalDps);
        Assert.Equal("358.8 / 412.5 tf", vm.Cpu);
        Assert.Equal(3, vm.ResistRows.Count);
        Assert.Equal("39%", vm.ResistRows[0].Em);
        Assert.StartsWith("Stable", vm.CapState);
        Assert.Equal("63.0 km", vm.TargetRange);
        Assert.False(vm.HasMiningYield);   // the combat sample has no mining yield -> panel hidden
    }

    [AvaloniaFact]
    public async Task DetailWindowVm_CpuOverBudget_GaugeKeepsBlueAndPulses_AndReadoutFlagsOver()
    {
        var fit = Fit("Thorax", 627, (2, "HiSlot0", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats(cpuUsed: 500.0)), sde: null, data: null);   // 500 > 412.5 output
        await vm.InitializeAsync();

        Assert.True(vm.CpuOver);      // readout turns red
        Assert.False(vm.PowerOver);   // 1064.6 < 1075.0 -> within budget

        var cpu = vm.RingGauges[0];   // CPU is the first gauge
        Assert.True(cpu.IsOverBudget);
        var brush = Assert.IsType<SolidColorBrush>(cpu.FillColor);
        Assert.Equal(Color.Parse("#7BACC3"), brush.Color);   // stays CPU blue, not the old red recolour
        Assert.Contains("CPU", cpu.Tooltip);
    }

    [Fact]
    public async Task DetailWindowVm_MiningFit_ShowsMiningYieldPanel()
    {
        var fit = Fit("Venture", 32880, (1, "HiSlot0", 1), (1, "HiSlot1", 1));

        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats() with { MiningYield = 9.0 }), sde: null, data: null);
        await vm.InitializeAsync();

        Assert.True(vm.HasMiningYield);
        Assert.Equal("9.0 m³/s", vm.MiningYield);
        Assert.Equal("540 m³/min", vm.MiningYieldPerMinute);   // 9.0 × 60
    }

    [Fact]
    public async Task MiningBreakdown_SplitsLasersAndDrones_FromContributions()
    {
        // The MINING yield's hover breakdown sums the mining-laser contributions and the mining-drone contributions
        // separately, mirroring the OFFENSE turrets/drones split.
        var stats = SampleStats() with
        {
            MiningYield = 12.5,
            ModuleContributions =
            [
                new ModuleContribution(100, ModuleContributionKind.Mining, ModuleState.Active, MiningYieldPerSec: 6.0),
                new ModuleContribution(100, ModuleContributionKind.Mining, ModuleState.Active, MiningYieldPerSec: 3.0),
                new ModuleContribution(8, ModuleContributionKind.Mining, ModuleState.Active, IsDrone: true, MiningYieldPerSec: 3.5)
            ]
        };
        var vm = new FitDetailWindowViewModel(Fit("Venture", 32880), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => stats), sde: null, data: null);
        await vm.InitializeAsync();

        Assert.Contains("Mining lasers: 9.0 m³/s", vm.MiningBreakdown);   // 6.0 + 3.0 summed across the two lasers
        Assert.Contains("Mining drones: 3.5 m³/s", vm.MiningBreakdown!);
    }

    [Fact]
    public async Task MiningBreakdown_OmitsDroneLine_WhenNoMiningDrones()
    {
        var stats = SampleStats() with
        {
            MiningYield = 6.0,
            ModuleContributions =
                [new ModuleContribution(100, ModuleContributionKind.Mining, ModuleState.Active, MiningYieldPerSec: 6.0)]
        };
        var vm = new FitDetailWindowViewModel(Fit("Venture", 32880), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => stats), sde: null, data: null);
        await vm.InitializeAsync();

        Assert.Contains("Mining lasers: 6.0 m³/s", vm.MiningBreakdown);
        Assert.DoesNotContain("drones", vm.MiningBreakdown!);
    }

    [Fact]
    public void Metadata_ExposesDescriptionAndParsedTagChips()
    {
        var fit = Fit("Hawk", 11993, (2, "HiSlot0", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance, provider: null, sde: null, data: null,
            description: "  Cheap brawler  ", tags: "pvp, cheap , , armor");

        Assert.True(vm.HasDescription);
        Assert.Equal("Cheap brawler", vm.Description);   // trimmed
        Assert.True(vm.HasTags);
        Assert.Equal(new[] { "pvp", "cheap", "armor" }, vm.TagChips);   // trimmed + blanks dropped
    }

    [Fact]
    public void Metadata_EmptyWhenNoneGiven()
    {
        var vm = new FitDetailWindowViewModel(Fit("Hawk", 11993), FallbackNameResolver.Instance, provider: null, sde: null, data: null);

        Assert.False(vm.HasDescription);
        Assert.False(vm.HasTags);
        Assert.Empty(vm.TagChips);
    }

    private sealed class FakeAttributesRepo(CharacterAttributes attributes) : ICharacterAttributesRepository
    {
        public Task ReplaceForCharacterAsync(CharacterAttributes a, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CharacterAttributes?> GetAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(characterId == attributes.CharacterId ? attributes : null);
    }

    private sealed class FakeImplantRepo(IReadOnlyList<int> implantTypeIds) : ICharacterImplantRepository
    {
        public Task ReplaceForCharacterAsync(int characterId, IReadOnlyList<int> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<int>> GetTypeIdsAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(implantTypeIds);
        public Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(implantTypeIds.Count > 0);
    }

    // A rank-1 skill (Perception/Willpower) a fitted module needs at V; the character has IV → one gap. typeIds: ship 587,
    // module 1000, skill 3300, +5 Perception implant 30000.
    private static async Task<FitDetailWindowViewModel> SkillGapVmAsync(IReadOnlyList<int> implantTypeIds)
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, 0, 0)
            .Type(1000, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], 3300),
                              new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 5))
            .Type(3300, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 1),
                              new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
                              new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower))
            .Type(30000, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.PerceptionBonus, 5));
        var skillRepo = new FakeSkillRepo(new Dictionary<int, IReadOnlyDictionary<int, int>> { [42] = new Dictionary<int, int> { [3300] = 4 } });
        var attributes = new CharacterAttributes { CharacterId = 42, Charisma = 20, Intelligence = 20, Memory = 20, Perception = 20, Willpower = 20 };

        var vm = new FitDetailWindowViewModel(Fit("Gap", 587, (1000, "HiSlot0", 1)), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: data, characters: [(42, "Sin Krah")],
            skillImporter: new FakeSkillImporter(SkillImportResult.Ok(1)), skillRepository: skillRepo,
            implantRepository: new FakeImplantRepo(implantTypeIds), attributesRepository: new FakeAttributesRepo(attributes));
        await vm.InitializeAsync();
        await vm.SelectSkillModeAsync(vm.SkillModes.Single(m => m.CharacterId == 42));
        return vm;
    }

    private static async Task<string?> SkillGapEstimateAsync(IReadOnlyList<int> implantTypeIds) =>
        (await SkillGapVmAsync(implantTypeIds)).SkillGaps.Single().Estimate;

    [Fact]
    public async Task SkillGap_MatchInGameRate_SwitchesToGenericBaseline()
    {
        // The character has 20/20 attributes (faster than the in-game panel's generic ~16.7 baseline). Toggling the rate
        // to "match in-game" must keep the SP identical (same skill) but lengthen the Omega time, and relabel the basis.
        var vm = await SkillGapVmAsync([]);
        var realRate = vm.SkillGaps.Single().Estimate;
        var realTotal = vm.SkillTrainingTotal;
        Assert.False(vm.HasEffectiveAttributes);                       // the character's own attributes are not surfaced
        Assert.Empty(vm.EffectiveAttributesLabel);

        vm.MatchInGameRate = true;

        var inGameRate = vm.SkillGaps.Single().Estimate;
        Assert.NotNull(realRate);
        Assert.NotNull(inGameRate);
        Assert.Contains("210.7k SP", realRate);                       // same SP both ways — only the rate changed
        Assert.Contains("210.7k SP", inGameRate);
        Assert.NotEqual(realRate, inGameRate);                        // the generic baseline trains slower → longer time
        Assert.NotEqual(realTotal, vm.SkillTrainingTotal);            // the panel total follows the same rate
        Assert.Contains("in-game", vm.EffectiveAttributesLabel.ToLowerInvariant());

        vm.MatchInGameRate = false;                                   // toggling back restores the character's own rate
        Assert.Equal(realRate, vm.SkillGaps.Single().Estimate);
    }

    [Fact]
    public async Task SkillGaps_CollapseToThree_AndToggleShowsAll()
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, 0, 0)
            .Type(1000, 0, 0,
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], 3300), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 5),
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[1], 3301), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[1], 5),
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[2], 3302), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[2], 5),
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[3], 3303), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[3], 5))
            .Type(3300, 0, 0).Type(3301, 0, 0).Type(3302, 0, 0).Type(3303, 0, 0);
        // A character id unique to this test — the skill store is shared across tests, so reusing 42 (FleetRoster's id)
        // would let this test's seeded skills bleed into other tests' expectations (test-isolation).
        const int charId = 8842;
        var skillRepo = new FakeSkillRepo(new Dictionary<int, IReadOnlyDictionary<int, int>>
            { [charId] = new Dictionary<int, int> { [3300] = 4, [3301] = 4, [3302] = 4, [3303] = 4 } });
        var attributes = new CharacterAttributes { CharacterId = charId, Charisma = 20, Intelligence = 20, Memory = 20, Perception = 20, Willpower = 20 };
        var vm = new FitDetailWindowViewModel(Fit("Gaps", 587, (1000, "HiSlot0", 1)), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: data, characters: [(charId, "Sin Krah")],
            skillImporter: new FakeSkillImporter(SkillImportResult.Ok(1)), skillRepository: skillRepo,
            implantRepository: new FakeImplantRepo([]), attributesRepository: new FakeAttributesRepo(attributes));
        await vm.InitializeAsync();
        await vm.SelectSkillModeAsync(vm.SkillModes.Single(m => m.CharacterId == charId));

        Assert.Equal(4, vm.SkillGaps.Count);
        Assert.True(vm.CanToggleSkills);
        Assert.Equal(3, vm.VisibleSkillGaps.Count);          // collapsed by default to the first 3
        Assert.Equal("Show all 4", vm.SkillsToggleLabel);

        vm.ShowAllSkills = true;
        Assert.Equal(4, vm.VisibleSkillGaps.Count);          // expanded shows every gap
        Assert.Equal("Show less", vm.SkillsToggleLabel);
    }

    [Fact]
    public async Task SkillGap_Estimate_ReflectsCharacterAttributesAndImplants()
    {
        var withoutImplant = await SkillGapEstimateAsync([]);
        var withImplant = await SkillGapEstimateAsync([30000]);   // +5 Perception implant raises the primary attribute

        Assert.NotNull(withoutImplant);
        Assert.NotNull(withImplant);
        Assert.Contains("210.7k SP", withoutImplant);                 // SP to train IV→V at rank 1
        Assert.Contains("210.7k SP", withImplant);                    // same SP — only the rate changed
        Assert.NotEqual(withoutImplant, withImplant);                 // the implant shortened the Omega time
    }

    [AvaloniaFact]
    public async Task FitDetail_Renders_SkillsRequired_WithEstimate()
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, 0, 0)
            .Type(1000, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], 3300),
                              new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 5))
            .Type(3300, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 1),
                              new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
                              new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower))
            .Type(30000, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.PerceptionBonus, 5));
        var skillRepo = new FakeSkillRepo(new Dictionary<int, IReadOnlyDictionary<int, int>> { [42] = new Dictionary<int, int> { [3300] = 4 } });
        var attributes = new CharacterAttributes { CharacterId = 42, Charisma = 20, Intelligence = 20, Memory = 20, Perception = 20, Willpower = 20 };

        var vm = new FitDetailWindowViewModel(Fit("Skill gap demo", 587, (1000, "HiSlot0", 1)), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: data, characters: [(42, "Sin Krah")],
            skillImporter: new FakeSkillImporter(SkillImportResult.Ok(1)), skillRepository: skillRepo,
            implantRepository: new FakeImplantRepo([30000]), attributesRepository: new FakeAttributesRepo(attributes));
        await vm.InitializeAsync();
        await vm.SelectSkillModeAsync(vm.SkillModes.Single(m => m.CharacterId == 42));

        Assert.True(vm.HasSkillGaps);
        Assert.True(vm.SkillGaps.Single().HasEstimate);
        Assert.True(vm.HasSkillTrainingTotal);
        Assert.Contains("SP", vm.SkillTrainingTotal);

        var window = new FitDetailWindow(vm) { Width = 900, Height = 600 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-skills-required.png");

        vm.MatchInGameRate = true;   // 1:1-with-in-game comparison rate (~25 SP/min generic baseline)
        var inGameFrame = window.CaptureRenderedFrame();
        Assert.NotNull(inGameFrame);
        inGameFrame!.Save("/tmp/eveutils-skills-required-ingame.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task FitDetail_Renders_WeatherSelector_InHeader()
    {
        // Iron Law #9 render-verify: the ENVIRONMENT dropdown sits in the header next to SKILLS/IMPLANTS and shows the
        // selected beacon (here a wormhole Pulsar). The engine effect is proven separately (DogmaEvaluator + wiring tests).
        var sde = new FakeSdeAccessor()
            .Add(587, "Rifter", 25, 6)
            .Add(30844, "Class 1 Pulsar Effects", 920, 2);
        var vm = new FitDetailWindowViewModel(Fit("Rifter", 587), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde, data: null);
        await vm.InitializeAsync();
        vm.WeatherSelector!.SelectedOption = vm.WeatherSelector.Options.Single(option => option.TypeId == 30844);

        var window = new FitDetailWindow(vm) { Width = 900, Height = 600 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-weather-selector.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task FitDetail_Renders_WithMetadata()
    {
        var fit = Fit("Hawk — PvP", 11993, (2, "HiSlot0", 1), (4, "MedSlot0", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance, provider: null, sde: null, data: null,
            description: "Cheap rocket brawler for low-stakes roams.", tags: "pvp, cheap, rockets");
        await vm.InitializeAsync();

        var window = new FitDetailWindow(vm) { Width = 900, Height = 600 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-detail-metadata.png");
        window.Close();
    }

    [Fact]
    public async Task DetailWindowVm_WithoutProvider_ShowsNotice()
    {
        var fit = Fit("No SDE", 627, (2, "HiSlot0", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance, provider: null, sde: null, data: null);
        await vm.InitializeAsync();

        Assert.False(vm.HasStats);
        Assert.Contains("SDE", vm.StatsNotice);
        Assert.Single(vm.RadialSlots); // the wheel still renders the slots
    }

    [Fact]
    public async Task DetailWindowVm_CyclingModuleState_RecomputesStats()
    {
        // Two modules, each able to go active (a category-1 effect). The provider counts modules at online-or-higher,
        // so offlining one must drop the count — proving the slot → input → provider → panel refresh round-trip.
        var fit = Fit("Thorax", 627, (2, "HiSlot0", 1), (4, "MedSlot0", 1));
        var data = new FakeDogmaDataAccessor()
            .Type(2, 74, 7).TypeEffect(2, 100).Effect(100, 1)
            .Type(4, 46, 7).TypeEffect(4, 101).Effect(101, 1);
        var provider = new StubStatsProvider(modules =>
            SampleStats(cpuUsed: modules.Count(m => m.State >= ModuleState.Online)));

        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance, provider, sde: null, data: data);
        await vm.InitializeAsync();

        Assert.Equal(2, vm.RadialSlots.Count);
        Assert.Equal(ModuleState.Active, vm.RadialSlots[0].State);
        Assert.Equal("2.0 / 412.5 tf", vm.Cpu);                 // both modules online-or-higher

        // Cycle the first module's state: valid order is [Passive, Online, Active] → Active wraps to Passive (offline).
        await vm.RadialSlots[0].CycleStateCommand.ExecuteAsync(null);

        Assert.Equal(ModuleState.Passive, vm.RadialSlots[0].State);
        Assert.Equal("1.0 / 412.5 tf", vm.Cpu);                 // the offline module no longer counts
    }

    [Fact]
    public async Task DetailWindowVm_SelectingTacticalMode_RecomputesWithMode()
    {
        var fit = Fit("Confessor", 34317);
        var data = new FakeDogmaDataAccessor()
            .Type(34317, 1305, 6)
            .TacticalModes(34317, new SdeNamedType(100, "Confessor Defense Mode"), new SdeNamedType(101, "Confessor Sharpshooter Mode"));
        var provider = new StubStatsProvider(_ => SampleStats());

        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance, provider, sde: null, data: data);
        await vm.InitializeAsync();

        Assert.True(vm.HasTacticalModes);
        Assert.Equal(2, vm.TacticalModes.Count);
        Assert.True(vm.TacticalModes[0].IsSelected);            // default stance = first (lowest type id)
        Assert.Equal(100, provider.LastTacticalModeTypeId);     // initial compute used the default mode

        vm.TacticalModes[1].Select.Execute(null);               // switch to the second stance
        Assert.True(vm.TacticalModes[1].IsSelected);
        Assert.False(vm.TacticalModes[0].IsSelected);
        Assert.Equal(101, provider.LastTacticalModeTypeId);     // recomputed with the new mode
    }

    [Fact]
    public async Task DetailWindowVm_ChargeModuleGroups_MergeSameType_FilterAndLoadOnAll()
    {
        // Two identical autocannons (charge group 83) on separate high slots + one missile launcher (group 386).
        var sde = new FakeSdeAccessor()
            .Add(587, "Rifter", 25, 6)
            .Add(2889, "200mm AutoCannon II", 55, 7, SdeSlotType.High, isTurret: true)
            .Add(12608, "EMP S", 83, 8).Add(12609, "Phased Plasma S", 83, 8)
            .Add(2410, "Heavy Missile Launcher II", 510, 7, SdeSlotType.High)
            .Add(209, "Scourge Heavy Missile", 386, 8);
        sde.Attr(2889, 604, 83);    // turret accepts charge group 83
        sde.Attr(2410, 604, 386);   // launcher accepts charge group 386

        var fit = Fit("Rifter", 587, (2889, "HiSlot0", 1), (2889, "HiSlot1", 1), (2410, "HiSlot2", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: sde, data: null);
        await vm.InitializeAsync();

        // The two identical turrets merge into one filter group; the launcher is a second group.
        Assert.Equal(2, vm.ChargeModuleGroups.Count);
        var turretGroup = vm.ChargeModuleGroups.Single(group => group.TypeId == 2889);
        var launcherGroup = vm.ChargeModuleGroups.Single(group => group.TypeId == 2410);

        // First group is selected by default; its charge list shows the two compatible turret charges.
        Assert.Same(turretGroup, vm.SelectedChargeGroup);
        Assert.True(turretGroup.IsSelected);
        Assert.Equal(2, vm.SelectedModuleCharges.Count);

        // Clicking the launcher filters the list to its single charge and moves the selection highlight.
        vm.SelectChargeModuleCommand.Execute(launcherGroup);
        Assert.Same(launcherGroup, vm.SelectedChargeGroup);
        Assert.True(launcherGroup.IsSelected);
        Assert.False(turretGroup.IsSelected);
        Assert.Single(vm.SelectedModuleCharges);

        // Loading a charge on the turret group applies it to every turret of the type.
        await turretGroup.LoadChargeAsync(12608);
        Assert.All(vm.RadialSlots.Where(slot => slot.TypeId == 2889),
            slot => Assert.Equal(12608, slot.ChargeTypeId));
    }

    [Fact]
    public async Task DetailWindowVm_Structure_LaysOutServiceSlots_AndHidesNavigation()
    {
        // A Raitaru (category 65 structure) with 3 service slots (hull attr 2056) carrying 2 service modules, plus a
        // high and a rig. The classifier routes ServiceSlot flags to the new service category; the hull's serviceSlots
        // count draws the ring so the third service slot stays an empty placeholder. The navigation panel is hidden —
        // a structure does not move.
        var data = new FakeDogmaDataAccessor()
            .Type(35825, 1404, 65,
                new SdeDogmaAttribute(14, 3), new SdeDogmaAttribute(13, 2), new SdeDogmaAttribute(12, 1),
                new SdeDogmaAttribute(1137, 3), new SdeDogmaAttribute(2056, 3),
                new SdeDogmaAttribute(2339, -25))   // structureServiceRoleBonus: -25% service-module fuel
            .Type(35878, 1404, 66, new SdeDogmaAttribute(2109, 12), new SdeDogmaAttribute(2110, 720))  // Manufacturing Plant: 12/h base
            .Type(35892, 1404, 66, new SdeDogmaAttribute(2109, 40), new SdeDogmaAttribute(2110, 720)); // second service: 40/h base
        var fit = Fit("Raitaru", 35825,
            (35878, "ServiceSlot0", 1), (35892, "ServiceSlot1", 1),
            (2, "HiSlot0", 1),
            (3, "RigSlot0", 1));

        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: data);
        await vm.InitializeAsync();

        Assert.True(vm.IsStructure);
        Assert.False(vm.HasNavigationPanel);                                   // immobile -> no velocity/align/warp panel
        var serviceSlots = vm.RadialSlots.Where(slot => slot.Category == FitSlotCategory.Service).ToList();
        Assert.Equal(2, serviceSlots.Count);                                   // both service modules sit on the ring
        Assert.Contains(serviceSlots, slot => slot.TypeId == 35878);
        Assert.Equal(4, vm.RadialSlots.Count);                                 // 2 service + 1 high + 1 rig (empties aside)

        // Fuel with the -25% role bonus: (12 + 40) base * 0.75 = 39 blocks/h; the fixed Upwell bay gives a days runtime.
        Assert.True(vm.HasFuel);
        Assert.Equal("39 blocks/h", vm.FuelConsumption);
        Assert.Contains("days", vm.FuelRuntime);   // 1,000,000-block bay / 39 per h

        // Offlining a service drops its draw live (Manufacturing Plant offline -> 40 base * 0.75 = 30 left).
        var manufacturing = serviceSlots.Single(slot => slot.TypeId == 35878);
        await manufacturing.CycleStateCommand.ExecuteAsync(null);
        Assert.Equal(ModuleState.Passive, manufacturing.State);
        Assert.Equal("30 blocks/h", vm.FuelConsumption);
        Assert.Contains(vm.FuelRows, row => row is { IsOnline: false } && row.FuelLabel == "offline");
    }

    // Records which type ids the wheel asks for, so the charge-vs-module rule and the opt-in gate are observable
    // without a real network/bitmap.
    private sealed class RecordingImageProvider(bool enabled) : ITypeImageProvider
    {
        public readonly List<int> Requested = [];
        public Task<bool> AreImagesEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(enabled);

        public Task<Bitmap?> GetImageAsync(int typeId, TypeImageKind kind, int size, CancellationToken cancellationToken = default)
        {
            Requested.Add(typeId);
            return Task.FromResult<Bitmap?>(null);
        }
    }

    [Fact]
    public async Task ModuleSlot_LoadImage_RequestsChargeIcon_WhenChargeLoaded()
    {
        var provider = new RecordingImageProvider(enabled: true);
        var slot = new ModuleSlotViewModel(2410, chargeTypeId: 209, FitSlotCategory.High, "Launcher", "M 0,0", 0, 0, "H",
            ModuleState.Active, [ModuleState.Online], [], provider, () => Task.CompletedTask);

        await slot.LoadImageAsync();

        Assert.Equal(209, Assert.Single(provider.Requested));   // the charge icon, not the module type
    }

    [Fact]
    public void ModuleSlot_SlotLabel_AbbreviatesCategoryWithOrdinal()
    {
        var slot = new ModuleSlotViewModel(2410, chargeTypeId: null, FitSlotCategory.Medium, "Afterburner", "M 0,0", 0, 0, "M",
            ModuleState.Active, [ModuleState.Online], [], images: null, () => Task.CompletedTask, slotNumber: 2);

        Assert.Equal("MID 2", slot.SlotLabel);
        Assert.Equal("MID 2", slot.TooltipModel.SlotLabel);
        Assert.True(slot.TooltipModel.HasSlotLabel);
    }

    [Fact]
    public void ModuleSlot_SlotLabel_IsEmpty_WhenNoSlotNumberSupplied()
    {
        var slot = new ModuleSlotViewModel(2410, chargeTypeId: null, FitSlotCategory.High, "Launcher", "M 0,0", 0, 0, "H",
            ModuleState.Active, [ModuleState.Online], [], images: null, () => Task.CompletedTask);

        Assert.Equal("", slot.SlotLabel);
        Assert.False(slot.TooltipModel.HasSlotLabel);
    }

    [Fact]
    public async Task EditMetadata_AppliesEditedDraftToHeader_ForLocalFit()
    {
        var edited = new FitMetadataDraft("Renamed Fit", "new notes", "pvp, solo");
        var vm = new FitDetailWindowViewModel(Fit("Rifter", 587), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: null,
            localFitId: 42, onEditMetadata: _ => Task.FromResult<FitMetadataDraft?>(edited));
        await vm.InitializeAsync();

        Assert.True(vm.CanEditMetadata);
        await ((IAsyncRelayCommand)vm.EditMetadataCommand).ExecuteAsync(null);

        Assert.Equal("Renamed Fit", vm.Name);
        Assert.Equal("new notes", vm.Description);
        Assert.Equal(new[] { "pvp", "solo" }, vm.TagChips);
        Assert.True(vm.HasTags);
    }

    [Fact]
    public async Task EditMetadata_IsDisabled_ForServerFitWithoutLocalId()
    {
        var vm = new FitDetailWindowViewModel(Fit("Rifter", 587), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: null);
        await vm.InitializeAsync();

        Assert.False(vm.CanEditMetadata);
    }

    [Fact]
    public void ChargeCompatibility_KeepsAcceptedGroupAndMatchingSize()
    {
        // Module accepts charge group 83 (chargeGroup1 attr 604) at size 1 (chargeSize attr 128).
        var sde = new FakeSdeAccessor()
            .Add(2873, "125mm Gatling AutoCannon II", 55, 7, SdeSlotType.High, isTurret: true)
            .Attr(2873, 604, 83).Attr(2873, 128, 1)
            .Add(12608, "EMP S", 83, 8).Attr(12608, 128, 1)            // group 83, size 1 -> compatible
            .Add(12625, "EMP M", 83, 8).Attr(12625, 128, 2)            // group 83, size 2 -> wrong size
            .Add(220, "Antimatter Charge S", 85, 8).Attr(220, 128, 1); // group 85 -> not accepted

        var charges = ChargeCompatibility.For(2873, sde);

        Assert.Equal(12608, Assert.Single(charges).TypeId);
    }

    [Fact]
    public void ModuleSlot_ChargeMenu_LoadsRemovesAndTogglesRemoveEntry()
    {
        var recomputes = 0;
        var infoRequests = new List<int>();
        var slot = new ModuleSlotViewModel(2873, chargeTypeId: null, FitSlotCategory.High, "AutoCannon", "M 0,0", 0, 0, "H",
            ModuleState.Active, [ModuleState.Online], [new SdeChargeType(12608, "EMP S", 1)], images: null,
            () => { recomputes++; return Task.CompletedTask; },
            id => { infoRequests.Add(id); return Task.CompletedTask; });

        // No charge yet: Information + the Charges submenu, but no Remove entry.
        Assert.Contains(slot.ChargeMenu, m => m.Label == "ℹ  Information");
        Assert.DoesNotContain(slot.ChargeMenu, m => m.Label.Contains("Remove charge"));

        // Charges are nested under a single "Charges" submenu.
        var chargesSubmenu = slot.ChargeMenu.First(m => m.Label == "Charges");
        chargesSubmenu.Children!.First(m => m.Label == "EMP S").Command!.Execute(null);   // load EMP S
        Assert.Equal(12608, slot.ChargeTypeId);
        Assert.True(recomputes >= 1);                                           // stats recomputed on charge change
        Assert.Contains(slot.ChargeMenu, m => m.Label.Contains("Remove charge"));      // Remove appears once charged
        Assert.Contains(slot.ChargeMenu, m => m.Label.Contains("Charge Information"));

        slot.ChargeMenu.First(m => m.Label == "ℹ  Information").Command!.Execute(null);
        slot.ChargeMenu.First(m => m.Label.Contains("Charge Information")).Command!.Execute(null);
        Assert.Equal(new[] { 2873, 12608 }, infoRequests);   // module info, then charge info

        slot.ChargeMenu.First(m => m.Label.Contains("Remove charge")).Command!.Execute(null);
        Assert.Null(slot.ChargeTypeId);
        Assert.DoesNotContain(slot.ChargeMenu, m => m.Label.Contains("Remove charge"));   // the reported bug: gone again
    }

    [Fact]
    public void ModuleSlot_Tooltip_ShowsTurretReadout_FromContribution()
    {
        // the tooltip renders the per-module contribution — charge, optimal/falloff range, DPS, the damage split
        // by type and tracking — closed by a state line, like the in-game module tooltip (ref tooltips/06).
        var slot = new ModuleSlotViewModel(2977, chargeTypeId: 21898, FitSlotCategory.High, "Heavy Pulse Laser II",
            "M 0,0", 0, 0, "H", ModuleState.Active, [ModuleState.Active],
            [new SdeChargeType(21898, "Scorch M", 1)], images: null, () => Task.CompletedTask);

        slot.SetContribution(new ModuleContribution(2977, ModuleContributionKind.Turret, ModuleState.Active,
            ChargeTypeId: 21898, Dps: 142.5, DamageEm: 78, DamageThermal: 64,
            OptimalRange: 24000, FalloffRange: 8000, TrackingSpeed: 0.071));

        var tooltip = slot.TooltipModel;
        Assert.Equal("Heavy Pulse Laser II", tooltip.Name);
        Assert.Equal("Scorch M", tooltip.ChargeName);
        Assert.Contains("Optimal 24.0 km", tooltip.Lines);
        Assert.Contains("Falloff 8.0 km", tooltip.Lines);
        Assert.Contains("Damage Per Second 142.5", tooltip.Lines);
        Assert.Contains("Tracking 0.071", tooltip.Lines);
        Assert.Equal("Active", tooltip.StateLabel);
        Assert.Contains(tooltip.DamageSegments, segment => segment.Label == "EM 78");
        Assert.Contains(tooltip.DamageSegments, segment => segment.Label == "TH 64");
    }

    [Fact]
    public async Task DetailWindowVm_LoadImages_FetchesNothing_WhenOptedOut()
    {
        var provider = new RecordingImageProvider(enabled: false);
        var fit = Fit("Thorax", 627, (2, "HiSlot0", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: null, images: provider);
        await vm.InitializeAsync();

        await vm.LoadImagesAsync();

        Assert.Empty(provider.Requested);   // opt-in off -> nothing fetched from the image server (local-first)
    }

    private sealed class FakeMarketPrices(IReadOnlyDictionary<int, double> prices) : IMarketPriceRepository
    {
        public Task ReplaceAllAsync(IReadOnlyCollection<LocalMarketPrice> p, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(prices.Count);

        public Task<IReadOnlyDictionary<int, double>> GetAveragePricesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<int, double>>(
                prices.Where(kv => typeIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    [Fact]
    public async Task DetailWindowVm_IskValue_SumsHullAndItemsFromMarketPrices()
    {
        var fit = Fit("Thorax", 627, (2, "HiSlot0", 1), (4, "MedSlot0", 1), (8, "DroneBay", 5));
        var prices = new FakeMarketPrices(new Dictionary<int, double>
            { [627] = 10_000_000, [2] = 1_000_000, [4] = 500_000, [8] = 200_000 });
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: null, images: null, prices: prices);

        await vm.InitializeAsync();

        // 10M hull + 1M high + 0.5M mid + 5 × 0.2M drones = 12.5M
        Assert.Equal("12.5 M ISK", vm.IskValue);
    }

    [Fact]
    public async Task Charges_UnionDeduped_AndDropOnAll_LoadsOnlyCompatibleModules()
    {
        // Two launchers accept EMP S (charge group 83, size 1); the damage control accepts nothing (2f drag-drop).
        var sde = new FakeSdeAccessor()
            .Add(2873, "125mm AutoCannon II", 55, 7, SdeSlotType.High, isTurret: true).Attr(2873, 604, 83).Attr(2873, 128, 1)
            .Add(2874, "150mm AutoCannon II", 55, 7, SdeSlotType.High, isTurret: true).Attr(2874, 604, 83).Attr(2874, 128, 1)
            .Add(2048, "Damage Control II", 60, 7, SdeSlotType.Low)
            .Add(12608, "EMP S", 83, 8).Attr(12608, 128, 1);
        var data = new FakeDogmaDataAccessor()
            .Type(2873, 55, 7).TypeEffect(2873, 10).Effect(10, 1)
            .Type(2874, 55, 7).TypeEffect(2874, 10).Effect(10, 1)
            .Type(2048, 60, 7);
        var fit = Fit("Rifter", 587, (2873, "HiSlot0", 1), (2874, "HiSlot1", 1), (2048, "LoSlot0", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance, new StubStatsProvider(_ => SampleStats()), sde, data);
        await vm.InitializeAsync();

        Assert.Equal(12608, Assert.Single(vm.AvailableCharges).TypeId);   // one compatible charge, deduped across both launchers

        await vm.LoadChargeOnAllAsync(12608);

        var launchers = vm.RadialSlots.Where(slot => slot.TypeId is 2873 or 2874).ToList();
        Assert.Equal(2, launchers.Count);
        Assert.All(launchers, slot => Assert.Equal(12608, slot.ChargeTypeId));            // loaded on every accepting module
        Assert.Null(vm.RadialSlots.Single(slot => slot.TypeId == 2048).ChargeTypeId);     // the DC doesn't accept it -> untouched
    }

    [Fact]
    public async Task Boosters_AddedViaPicker_AppliedAsImplant_ThenToggledAndRemoved()
    {
        // A booster is any SDE type carrying boosterness (attr 1087); the picker lists them and adding one applies it as
        // a char-anchored implant in the recompute. Toggling it off suspends it; removing it drops it entirely.
        var sde = new FakeSdeAccessor()
            .Add(587, "Rifter", 25, 6)
            .Add(10000, "Strong Blue Pill Booster", 303, 20).Attr(10000, 1087, 1);
        var provider = new StubStatsProvider(_ => SampleStats());
        var vm = new FitDetailWindowViewModel(Fit("Rifter", 587), FallbackNameResolver.Instance, provider, sde, data: null);
        await vm.InitializeAsync();

        Assert.True(vm.HasBoosterPicker);
        Assert.Equal("Strong Blue Pill Booster", Assert.Single(vm.BoosterMenu).Label);   // the one booster type is offered

        vm.BoosterMenu[0].Command!.Execute(null);                                  // add it
        Assert.Single(vm.Boosters);
        Assert.Equal(10000, Assert.Single(provider.LastBoosters!).TypeId);        // applied as an implant in the recompute

        vm.Boosters[0].IsActive = false;                                          // suspend it
        Assert.Empty(provider.LastBoosters!);                                      // no longer applied

        vm.Boosters[0].RemoveCommand.Execute(null);                               // remove it
        Assert.Empty(vm.Boosters);
    }

    [Fact]
    public async Task Weather_SelectingBeacon_ThreadedIntoRecompute_NoneClearsIt()
    {
        // The header weather picker offers None + the curated effect beacons (a group-920 Pulsar here). Selecting one
        // threads its beacon type into the recompute; None injects no weather source so the result is unchanged.
        var sde = new FakeSdeAccessor()
            .Add(587, "Rifter", 25, 6)
            .Add(30844, "Class 1 Pulsar Effects", 920, 2);
        var provider = new StubStatsProvider(_ => SampleStats());
        var vm = new FitDetailWindowViewModel(Fit("Rifter", 587), FallbackNameResolver.Instance, provider, sde, data: null);
        await vm.InitializeAsync();

        Assert.Equal(["None", "Pulsar C1"], vm.WeatherSelector!.Options.Select(option => option.Label));
        Assert.Null(provider.LastWeather);   // default None -> no weather source

        vm.WeatherSelector.SelectedOption = vm.WeatherSelector.Options.Single(option => option.TypeId == 30844);
        Assert.Equal(30844, provider.LastWeather?.TypeId);   // threaded through the recompute

        vm.WeatherSelector.SelectedOption = vm.WeatherSelector.Options[0];   // back to None
        Assert.Null(provider.LastWeather);
    }

    [Fact]
    public async Task StorageBays_ListsCargoFromCapacityAndSpecialHolds_FormattedM3_HiddenWhenNone()
    {
        // Cargo comes from the Type.capacity column; the special holds from dogma attributes; cargo is listed first.
        var data = new FakeDogmaDataAccessor()
            .Type(28606, 941, 6, new SdeDogmaAttribute(1556, 150000), new SdeDogmaAttribute(912, 40000))   // Orca: ore + fleet hangar
            .Cargo(28606, 30000, 0);
        var vm = new FitDetailWindowViewModel(Fit("Orca", 28606), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: data);
        await vm.InitializeAsync();

        Assert.True(vm.HasStorageBays);
        Assert.Equal(["Cargo Hold", "Ore Hold", "Fleet Hangar"], vm.StorageBays.Select(bay => bay.Name));
        Assert.Equal(["30,000 m³", "150,000 m³", "40,000 m³"], vm.StorageBays.Select(bay => bay.Volume));

        // A hull with no cargo and no special holds hides the panel.
        var bare = new FitDetailWindowViewModel(Fit("Rifter", 587), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: new FakeDogmaDataAccessor().Type(587, 25, 6));
        await bare.InitializeAsync();
        Assert.False(bare.HasStorageBays);
        Assert.Empty(bare.StorageBays);
    }

    [Fact]
    public async Task StorageBays_Structure_ShowsFuelBay_DespiteNoDogmaBays()
    {
        // A category-65 structure carries no bay attributes and no Type.capacity; its fuel bay is hard-coded, so the
        // panel still reflects that it has storage (the Raitaru feedback, 2026-06-12).
        var data = new FakeDogmaDataAccessor().Type(35825, 1404, 65);   // Raitaru: structure, no bays
        var vm = new FitDetailWindowViewModel(Fit("Raitaru", 35825), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: data);
        await vm.InitializeAsync();

        Assert.True(vm.HasStorageBays);
        var fuel = Assert.Single(vm.StorageBays);
        Assert.Equal("Fuel Bay", fuel.Name);
        Assert.Equal("5,000,000 m³", fuel.Volume);
    }

    [AvaloniaFact]
    public async Task FitDetail_Renders_StoragePanel()
    {
        // Iron Law #9 render-verify: the STORAGE panel sits in the right stats column with a row per non-zero bay.
        var data = new FakeDogmaDataAccessor()
            .Type(28606, 941, 6, new SdeDogmaAttribute(1556, 150000), new SdeDogmaAttribute(912, 40000),
                new SdeDogmaAttribute(908, 400000))
            .Cargo(28606, 30000, 0);
        var vm = new FitDetailWindowViewModel(Fit("Orca", 28606), FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: data);
        await vm.InitializeAsync();

        var window = new FitDetailWindow(vm) { Width = 900, Height = 1150 };   // tall so the scrollable stats column shows STORAGE
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-storage-panel.png");
        window.Close();
    }

    [Fact]
    public async Task DroneBay_SelectedCheckboxRow_DeploysAndRecalls_AndSyncsBoxes()
    {
        // The in-game "Selected:" checkbox row drives which drones are in space: checking box P deploys P, unchecking it
        // recalls to P-1. With no bandwidth data only the universal 5-drone cap applies, so a full bay of 5 starts deployed.
        var fit = Fit("Drone Thorax", 627, (2, "HiSlot0", 1), (8, "DroneBay", 5));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: null);
        await vm.InitializeAsync();

        var stack = Assert.Single(vm.DroneBay);
        Assert.Equal(5, stack.Slots.Count);                                       // one checkbox per drone in the bay
        Assert.Equal(5, stack.ActiveQuantity);                                    // 5-cap fills the bay by default
        Assert.All(stack.Slots, slot => Assert.True(slot.IsSelected));
        Assert.Equal("5 / 5", vm.DroneActiveLabel);

        stack.Slots[2].IsSelected = false;                                        // uncheck the 3rd -> recall to 2
        Assert.Equal(2, stack.ActiveQuantity);
        Assert.Equal(new[] { true, true, false, false, false }, stack.Slots.Select(slot => slot.IsSelected).ToArray());
        Assert.Equal("2 / 5", vm.DroneActiveLabel);

        stack.Slots[3].IsSelected = true;                                         // check the 4th -> deploy back up to 4
        Assert.Equal(4, stack.ActiveQuantity);
        Assert.Equal(new[] { true, true, true, true, false }, stack.Slots.Select(slot => slot.IsSelected).ToArray());
    }

    private sealed class FakeSkillImporter(SkillImportResult result) : IEsiSkillImporter
    {
        public int? LastCharacterId { get; private set; }

        public Task<SkillImportResult> ImportAsync(int characterId, CancellationToken cancellationToken = default)
        {
            LastCharacterId = characterId;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSkillRepo(Dictionary<int, IReadOnlyDictionary<int, int>> store) : ICharacterSkillRepository
    {
        public Task ReplaceForCharacterAsync(int characterId, IReadOnlyDictionary<int, int> levels, CancellationToken cancellationToken = default)
        {
            store[characterId] = levels;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<int, int>> GetLevelsAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(store.TryGetValue(characterId, out var levels) ? levels : new Dictionary<int, int>());

        public Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(store.ContainsKey(characterId));
    }

    [Fact]
    public async Task SkillSelector_ChoosingCharacter_ImportsAndAppliesSkills_AllVResets()
    {
        var importer = new FakeSkillImporter(SkillImportResult.Ok(2));
        var repo = new FakeSkillRepo(new Dictionary<int, IReadOnlyDictionary<int, int>>
            { [42] = new Dictionary<int, int> { [3300] = 5, [3301] = 4 } });
        var provider = new StubStatsProvider(_ => SampleStats());
        var vm = new FitDetailWindowViewModel(Fit("Rifter", 587), FallbackNameResolver.Instance, provider,
            sde: null, data: null, characters: [(42, "Sin Krah")], skillImporter: importer, skillRepository: repo);
        await vm.InitializeAsync();

        Assert.True(vm.HasSkillCharacters);
        Assert.Equal(6, vm.SkillModes.Count);                                  // All I..V + one character
        Assert.Equal(5, vm.SelectedSkillMode!.AllLevel);                       // All V is the default
        Assert.Null(provider.LastSkills);                                      // the initial compute uses the all-V baseline

        // Picking a lower all-skills level applies a uniform baseline.
        await vm.SelectSkillModeAsync(vm.SkillModes.Single(m => m.AllLevel == 3));
        Assert.True(provider.LastSkills!.InjectsAllSkills);
        Assert.Equal(3, provider.LastSkills.LevelFor(12345));                  // every skill at level 3

        // Picking the character imports + applies its real levels.
        await vm.SelectSkillModeAsync(vm.SkillModes.Single(m => m.CharacterId == 42));
        Assert.Equal(42, importer.LastCharacterId);
        Assert.Equal(42, vm.SelectedSkillMode!.CharacterId);                   // the dropdown reflects the choice
        Assert.False(provider.LastSkills!.InjectsAllSkills);                   // character levels, not a baseline
        Assert.Equal(5, provider.LastSkills.LevelFor(3300));
        Assert.Equal(0, provider.LastSkills.LevelFor(99999));                  // an untrained skill is 0

        // Back to All V.
        await vm.SelectSkillModeAsync(vm.SkillModes.Single(m => m.AllLevel == 5));
        Assert.True(provider.LastSkills!.InjectsAllSkills);
        Assert.Equal(5, provider.LastSkills.LevelFor(12345));
    }

    [Fact]
    public async Task SkillSelector_RemembersMode_AppliesOnOpen_AndPersistsOnChange()
    {
        string? persisted = null;
        var provider = new StubStatsProvider(_ => SampleStats());
        var vm = new FitDetailWindowViewModel(Fit("Rifter", 587), FallbackNameResolver.Instance, provider,
            sde: null, data: null,
            rememberedSkillMode: "all:3",
            onSkillModeChanged: value => { persisted = value; return Task.CompletedTask; });
        await vm.InitializeAsync();

        Assert.Equal(3, vm.SelectedSkillMode!.AllLevel);                     // the remembered mode is restored on open
        Assert.True(provider.LastSkills!.InjectsAllSkills);
        Assert.Equal(3, provider.LastSkills.LevelFor(12345));                 // and applied to the initial compute

        await vm.SelectSkillModeAsync(vm.SkillModes.Single(m => m.AllLevel == 4));
        Assert.Equal("all:4", persisted);                                    // a change is persisted via the callback
    }

    [AvaloniaFact]
    public void FitDetail_Renders()
    {
        var fit = Fit("Thorax PVE Shield", 627,
            (2, "HiSlot0", 1), (2, "HiSlot1", 1), (2, "HiSlot2", 1),
            (4, "MedSlot0", 1), (4, "MedSlot1", 1),
            (5, "LoSlot0", 1), (5, "LoSlot1", 1),
            (8, "DroneBay", 5));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: null);
        vm.InitializeAsync().GetAwaiter().GetResult();

        var window = new FitDetailWindow(vm) { Width = 1080, Height = 680 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-detail.png");
    }

    [AvaloniaFact]
    public void FitDetail_Structure_Renders()
    {
        // A Raitaru with the real slot counts (hi 3 / med 2 / low 1 / rig 3 / service 3) so the service-slot arc and the
        // empty placeholders render — for eyeballing the structure radial layout (service-slot placement is provisional).
        var data = new FakeDogmaDataAccessor()
            .Type(35825, 1404, 65,
                new SdeDogmaAttribute(14, 3), new SdeDogmaAttribute(13, 2), new SdeDogmaAttribute(12, 1),
                new SdeDogmaAttribute(1137, 3), new SdeDogmaAttribute(2056, 3))
            .Type(35878, 1404, 66, new SdeDogmaAttribute(2109, 12), new SdeDogmaAttribute(2110, 720))
            .Type(35892, 1404, 66, new SdeDogmaAttribute(2109, 40), new SdeDogmaAttribute(2110, 720))
            .Type(35886, 1404, 66, new SdeDogmaAttribute(2109, 12), new SdeDogmaAttribute(2110, 720));
        var fit = Fit("Raitaru", 35825,
            (35878, "ServiceSlot0", 1), (35892, "ServiceSlot1", 1), (35886, "ServiceSlot2", 1),
            (2, "HiSlot0", 1), (2, "HiSlot1", 1),
            (4, "MedSlot0", 1),
            (5, "LoSlot0", 1),
            (3, "RigSlot0", 1), (3, "RigSlot1", 1));
        var vm = new FitDetailWindowViewModel(fit, FallbackNameResolver.Instance,
            new StubStatsProvider(_ => SampleStats()), sde: null, data: data);
        vm.InitializeAsync().GetAwaiter().GetResult();

        var window = new FitDetailWindow(vm) { Width = 1080, Height = 680 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-detail-structure.png");
    }
}
