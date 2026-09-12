using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>What one section is on each screen: the run window's module, the detail screen's, or both. Its view is
/// found by the app's <c>ViewLocator</c> from the view model's name, so nothing else has to list it.</summary>
/// <param name="IskSource">The share of TOTAL ISK this section shows (ET-256) — its contributor in
/// <c>IskContributors</c> adds it up; the section never does. Null for a section that shows no ISK of the run's own.</param>
/// <param name="HasLiveContent">ET-228's extension of the detail screen's own ET-162 rule ("an unclaimed section
/// with real content still shows") to the run window: a type that does not claim this section still gets it, live,
/// once this comes back true — a combat homefront where someone starts mining shows MINING mid-run instead of only
/// after SAVE. Null for a section with no such reactive claim, which is every one but MINING today: ENEMIES/BOUNTY/
/// LOOT are already claimed by every site type, and the rest are type-specific facts (FIT, CONSUMABLES, a mission's
/// own section) nothing else could ever have content for.</param>
public sealed record RunSectionModule(
    RunSectionId Id,
    Func<IRunWindowContext, RunWindowSection>? CreateForWindow,
    Func<RunDetailSectionServices, RunDetailSection>? CreateForDetail,
    IskSource? IskSource = null,
    Func<IRunWindowContext, bool>? HasLiveContent = null);

/// <summary>
/// Every section module, in the order both screens draw them (ET-236). A run type only says which sections it has
/// (<see cref="RunTypeCatalogue"/>); where each one stands is decided here, once, so two types can never put the
/// same sections in a different order.
/// </summary>
public static class RunSectionModules
{
    public static IReadOnlyList<RunSectionModule> All { get; } =
    [
        new(RunSectionId.Activity,
            context => new ActivityWindowSectionViewModel(context),
            services => new ActivityDetailSectionViewModel(services.Sde)),
        // Straight under ACTIVITY, where the money of a homefront is decided (ET-230, mockup pin 4). No ISK source of
        // its own yet: the fixed payout per ticked character is ET-231's contributor, which reads N and the tick.
        new(RunSectionId.Homefront,
            context => new HomefrontWindowSectionViewModel(context),
            services => new HomefrontDetailSectionViewModel(services)),
        new(RunSectionId.Mission,
            context => new MissionWindowSectionViewModel(context),
            services => new MissionDetailSectionViewModel(services.Sde),
            IskSource.Rewards),
        new(RunSectionId.Enemies,
            context => new EnemiesWindowSectionViewModel(context),
            _ => new EnemiesDetailSectionViewModel()),
        new(RunSectionId.Fit, context => new FitWindowSectionViewModel(context), null),
        new(RunSectionId.Fleet,
            context => new FleetWindowSectionViewModel(context),
            services => new FleetDetailSectionViewModel(services.NameOf)),
        new(RunSectionId.Bounty,
            context => new BountyWindowSectionViewModel(context),
            _ => new BountyDetailSectionViewModel(),
            IskSource.Bounty),
        new(RunSectionId.Loot,
            context => new LootWindowSectionViewModel(context),
            services => new LootDetailSectionViewModel(services),
            IskSource.Loot),
        new(RunSectionId.Mining,
            context => new MiningWindowSectionViewModel(context),
            services => new MiningDetailSectionViewModel(services),
            IskSource.Mining,
            // A combat/data/relic/gas/wormhole/homefront run where someone mined still shows it live, the same
            // "after the fact" the detail screen already gives every type for MINING (ET-229, ET-236's ET-162
            // rule) — extended here to the window itself rather than waiting for SAVE.
            HasLiveContent: context => context.Participants.Any(participant => participant.MiningEntries.Count > 0)),
        new(RunSectionId.Consumables,
            context => new ConsumablesWindowSectionViewModel(context),
            _ => new ConsumablesDetailSectionViewModel(),
            IskSource.Consumables),
        new(RunSectionId.Escalation, null, services => new EscalationDetailSectionViewModel(services))
    ];
}
