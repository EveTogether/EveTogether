using System;
using System.Linq;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;

namespace EveUtils.Client.UiTests;

/// <summary>A test's way to one section of a run screen (ET-236): the screens themselves only hold them as the list
/// their type claims. Throws rather than returning null — a test that asks for a section the type does not draw has
/// already found what it is looking for.</summary>
internal static class RunSectionAccess
{
    public static ActivityWindowSectionViewModel Activity(this ActivityWindowViewModel window) => window._Section<ActivityWindowSectionViewModel>();
    public static EnemiesWindowSectionViewModel Enemies(this ActivityWindowViewModel window) => window._Section<EnemiesWindowSectionViewModel>();
    public static FitWindowSectionViewModel Fit(this ActivityWindowViewModel window) => window._Section<FitWindowSectionViewModel>();
    public static FleetWindowSectionViewModel Fleet(this ActivityWindowViewModel window) => window._Section<FleetWindowSectionViewModel>();
    public static BountyWindowSectionViewModel Bounty(this ActivityWindowViewModel window) => window._Section<BountyWindowSectionViewModel>();
    public static LootWindowSectionViewModel Loot(this ActivityWindowViewModel window) => window._Section<LootWindowSectionViewModel>();

    public static ActivityDetailSectionViewModel Activity(this ActivityDetailViewModel detail) => detail._Section<ActivityDetailSectionViewModel>();
    public static RewardsDetailSectionViewModel Rewards(this ActivityDetailViewModel detail) => detail._Section<RewardsDetailSectionViewModel>();
    public static EnemiesDetailSectionViewModel Enemies(this ActivityDetailViewModel detail) => detail._Section<EnemiesDetailSectionViewModel>();
    public static FleetDetailSectionViewModel Fleet(this ActivityDetailViewModel detail) => detail._Section<FleetDetailSectionViewModel>();
    public static BountyDetailSectionViewModel Bounty(this ActivityDetailViewModel detail) => detail._Section<BountyDetailSectionViewModel>();
    public static LootDetailSectionViewModel Loot(this ActivityDetailViewModel detail) => detail._Section<LootDetailSectionViewModel>();
    public static EscalationDetailSectionViewModel Escalation(this ActivityDetailViewModel detail) => detail._Section<EscalationDetailSectionViewModel>();

    private static T _Section<T>(this ActivityWindowViewModel window) where T : ActivitySection =>
        window.Sections.OfType<T>().SingleOrDefault()
        ?? throw new InvalidOperationException($"The run window does not draw {typeof(T).Name} for this type.");

    private static T _Section<T>(this ActivityDetailViewModel detail) where T : ActivitySection =>
        detail.Sections.OfType<T>().SingleOrDefault()
        ?? throw new InvalidOperationException($"The detail screen does not draw {typeof(T).Name} for this activity.");
}
