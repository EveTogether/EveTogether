using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The single-select fit picker + composition scope: a coupled composition's allowed fits
/// group by role on a Composition source, the current assignment is marked and inert, picking a row raises the pick
/// immediately, and Local/Server stay available for an own fit.
/// </summary>
public class FitPickerSingleModeTests
{
    private const int Owner = 95400001;

    private static async Task SeedCharacterAsync(IServiceProvider services, int characterId, string name) =>
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));

    private static async Task SeedLocalFitAsync(IServiceProvider services, int owner, string name, int shipTypeId, string contentHash) =>
        await services.GetRequiredService<IFittingRepository>().UpsertAsync(new LocalFitting
        {
            OwnerId = owner.ToString(), EsiFittingId = shipTypeId, Name = name, ShipTypeId = shipTypeId,
            RawJson = "{}", ContentHash = contentHash, ImportedAt = DateTimeOffset.UtcNow
        });

    private static FleetCompositionDetail Doctrine()
    {
        FitReferenceInfo Fit(string name, int ship, string hash) => new(ship, name, "{}", hash, null, null);
        var dps = new FleetCompositionRoleInfo(1, 1, "DPS", 40, 0, new[]
        {
            new FleetCompositionEntryInfo(1, 1, null, 0, Fit("Muninn — Kite", 12005, "h-muninn")),
            new FleetCompositionEntryInfo(2, 1, null, 1, Fit("Eagle — Rail", 12011, "h-eagle"))
        });
        var logi = new FleetCompositionRoleInfo(2, 1, "Logistics", null, 1, new[]
        {
            new FleetCompositionEntryInfo(3, 2, 3, 0, Fit("Guardian — Armor", 11987, "h-guardian"))
        });
        return new FleetCompositionDetail(
            new FleetCompositionInfo(1, "Homefront Vanguard", null, Owner, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new[] { dps, logi });
    }

    [AvaloniaFact]
    public async Task Single_CompositionScope_GroupsByRole_AndPicksImmediately()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var vm = new FitPickerViewModel(instance.Services, FitPickerMode.Single, alreadyAdded: null, composition: Doctrine(), currentFitHash: null);
        await vm.EnsureLoadedAsync();

        Assert.True(vm.IsSingle);
        Assert.True(vm.HasComposition);
        Assert.True(vm.IsCompositionSource);                     // scoped picker defaults to the doctrine
        Assert.Equal(new[] { "DPS", "Logistics" }, vm.RoleGroups.Select(g => g.RoleName).ToArray());
        Assert.Equal("≥40", vm.RoleGroups[0].MinLabel);

        FitReferenceInfo? picked = null;
        vm.FitPicked += f => picked = f;
        vm.ActivateRowCommand.Execute(vm.RoleGroups[0].Rows.First());   // single-select picks immediately

        Assert.NotNull(picked);
        Assert.Equal("Muninn — Kite", picked!.FitName);
    }

    [AvaloniaFact]
    public async Task Single_CurrentFit_IsMarked_AndInert()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var vm = new FitPickerViewModel(instance.Services, FitPickerMode.Single, alreadyAdded: null, composition: Doctrine(), currentFitHash: "h-guardian");
        await vm.EnsureLoadedAsync();

        var guardian = vm.RoleGroups.SelectMany(g => g.Rows).Single(r => r.FitName == "Guardian — Armor");
        Assert.True(guardian.IsCurrent);

        FitReferenceInfo? picked = null;
        vm.FitPicked += f => picked = f;
        vm.ActivateRowCommand.Execute(guardian);   // the current assignment is not re-pickable
        Assert.Null(picked);
    }

    [AvaloniaFact]
    public async Task Single_SwitchesToLocalSource_ForAnOwnFit()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedLocalFitAsync(instance.Services, Owner, "Vexor — Drone", 17843, "h-vexor");

        var vm = new FitPickerViewModel(instance.Services, FitPickerMode.Single, alreadyAdded: null, composition: Doctrine(), currentFitHash: null);
        await vm.EnsureLoadedAsync();

        vm.ShowLocalCommand.Execute(null);
        Assert.True(vm.IsLocalSource);
        Assert.Contains(vm.Rows, r => r.FitName == "Vexor — Drone");
    }

    // picker badge: warning for the Muninn, can-fly for the Eagle, no verdict for anything else (keyed on the fit hash).
    private sealed class StubFitSkillEvaluator : IMemberFitSkillEvaluator
    {
        public Task<MemberSkillBadge?> EvaluateAsync(int characterId, FitReferenceInfo? assignedFit) =>
            Task.FromResult<MemberSkillBadge?>(assignedFit?.ContentHash switch
            {
                "h-muninn" => new MemberSkillBadge(false, "1 skill missing"),
                "h-eagle" => new MemberSkillBadge(true, "Can fly this fit"),
                _ => null
            });
    }

    private static async Task WaitForBadgeAsync(FitPickerRowViewModel row)
    {
        for (var i = 0; i < 100 && row.SkillBadge is null; i++) await Task.Delay(20);
    }

    [AvaloniaFact]
    public async Task Single_ShowsCanFlyBadgePerRow_AgainstTheTargetCharacter()
    {
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IMemberFitSkillEvaluator>(new StubFitSkillEvaluator()));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var vm = new FitPickerViewModel(instance.Services, FitPickerMode.Single, alreadyAdded: null,
            composition: Doctrine(), currentFitHash: null, skillCheckCharacterId: 42);
        await vm.EnsureLoadedAsync();

        var muninn = vm.RoleGroups.SelectMany(g => g.Rows).Single(r => r.FitName == "Muninn — Kite");
        var eagle = vm.RoleGroups.SelectMany(g => g.Rows).Single(r => r.FitName == "Eagle — Rail");
        await WaitForBadgeAsync(muninn);
        await WaitForBadgeAsync(eagle);

        Assert.True(muninn.HasSkillGap);
        Assert.False(muninn.CanFly);
        Assert.Equal("1 skill missing", muninn.SkillBadgeTooltip);
        Assert.True(eagle.CanFly);
        Assert.False(eagle.HasSkillGap);
    }

    [AvaloniaFact]
    public async Task Single_NoSkillCheckCharacter_ShowsNoBadges()
    {
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IMemberFitSkillEvaluator>(new StubFitSkillEvaluator()));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        // No skillCheckCharacterId (e.g. the composition editor) → the evaluator is never consulted.
        var vm = new FitPickerViewModel(instance.Services, FitPickerMode.Single, alreadyAdded: null, composition: Doctrine(), currentFitHash: null);
        await vm.EnsureLoadedAsync();

        var muninn = vm.RoleGroups.SelectMany(g => g.Rows).Single(r => r.FitName == "Muninn — Kite");
        await Task.Delay(60);   // give any (unexpected) badge load a chance to run
        Assert.Null(muninn.SkillBadge);
        Assert.False(muninn.HasSkillGap);
    }

    [AvaloniaFact]
    public async Task FitPickerWindow_SingleComposition_Renders()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var vm = new FitPickerViewModel(instance.Services, FitPickerMode.Single, alreadyAdded: null, composition: Doctrine(), currentFitHash: "h-guardian");
        await vm.EnsureLoadedAsync();

        var window = new FitPickerWindow(vm) { Width = 560, Height = 580 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-picker-single.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task FitPickerWindow_WithCanFlyBadges_Renders()
    {
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IMemberFitSkillEvaluator>(new StubFitSkillEvaluator()));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var vm = new FitPickerViewModel(instance.Services, FitPickerMode.Single, alreadyAdded: null,
            composition: Doctrine(), currentFitHash: null, skillCheckCharacterId: 42);
        await vm.EnsureLoadedAsync();
        await WaitForBadgeAsync(vm.RoleGroups.SelectMany(g => g.Rows).Single(r => r.FitName == "Muninn — Kite"));

        var window = new FitPickerWindow(vm) { Width = 560, Height = 580 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-picker-skill-badges.png");
        window.Close();
    }
}
