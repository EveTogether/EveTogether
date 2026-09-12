using System;
using System.Collections.Generic;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>What one section is on each screen: the run window's module, the detail screen's, or both. Its view is
/// found by the app's <c>ViewLocator</c> from the view model's name, so nothing else has to list it.</summary>
/// <param name="IskSource">The share of TOTAL ISK this section shows (ET-256) — its contributor in
/// <c>IskContributors</c> adds it up; the section never does. Null for a section that shows no ISK of the run's own.</param>
public sealed record RunSectionModule(
    RunSectionId Id,
    Func<IRunWindowContext, RunWindowSection>? CreateForWindow,
    Func<RunDetailSectionServices, RunDetailSection>? CreateForDetail,
    IskSource? IskSource = null);

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
            IskSource.Mining),
        new(RunSectionId.Consumables,
            context => new ConsumablesWindowSectionViewModel(context),
            _ => new ConsumablesDetailSectionViewModel(),
            IskSource.Consumables),
        new(RunSectionId.Escalation, null, services => new EscalationDetailSectionViewModel(services))
    ];
}
