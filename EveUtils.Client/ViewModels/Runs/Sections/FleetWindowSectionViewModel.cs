using System;
using System.Collections.ObjectModel;
using System.Linq;
using EveUtils.Client.ViewModels.Activity;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// FLEET in the run window: who the window has heard from, what they shared, and who of this pilot's own toons is on
/// the run with a share of it (ET-105). Gone rather than empty when no fleet has ever reported in: a FLEET section
/// standing open on a solo run reads as a measurement, and nothing here can measure the absence of a fleet.
/// </summary>
public sealed class FleetWindowSectionViewModel(IRunWindowContext context)
    : RunWindowSection(context, RunSectionId.Fleet, "FLEET")
{
    public override bool IsShown => Context.IsFleetShown;

    /// <summary>Who this window has actually heard from, one row per member that sent a sample. Never a roster:
    /// nothing here can see a member who is not sharing, which is what <see cref="FleetBasisText"/> says.</summary>
    public ObservableCollection<ActivityFleetMemberViewModel> FleetMembers => Context.FleetMembers;

    public ObservableCollection<RunParticipantViewModel> Participants => Context.Participants;

    /// <summary>Shown over every payout figure. The window reports an expectation, and never implies EVE's own
    /// payout rule follows our exclusions. At a homefront the "share" box splits bounty and loot only — the
    /// homefront's own payout is per character and not split at all (ET-230), so the caption must not say otherwise.</summary>
    public string PayoutExpectationLabel => Context.RunType.PaysPerCharacterInSite
        ? RunPayoutSplit.HomefrontShareLabel
        : RunPayoutSplit.ExpectationLabel;

    /// <summary>
    /// What the count is counted from, said outright. The fleet count counts samples, so a member who does not share
    /// is missing from both the number and the list — and a list of two names in a fleet of three is a lie unless it
    /// says what it is a list of.
    /// </summary>
    public string FleetBasisText => FleetMembers.Count == 0
        ? "No member has shared anything yet, so there is nobody to list."
        : "Counted from what members share. A member sharing nothing is in the fleet but not in this list.";

    /// <summary>
    /// What the rows above add up to, and only them. That is why it stands between the names and
    /// <see cref="FleetBasisText"/>: a total that covered more than the rows it sits under would need explaining,
    /// and the caption below is already the line that says the fleet may be larger than this list. A member sharing
    /// neither figure is in neither the rows nor the sum, which is the same rule in both places.
    ///
    /// Never a zero for a figure nobody offered — the two halves are counted apart, so a fleet sharing bounty and no
    /// loot says exactly that rather than reporting nothing looted.
    /// </summary>
    public string FleetTotalText => (_FleetSum(row => row.LootIsk), _FleetSum(row => row.BountyIsk)) switch
    {
        (null, null) => "no member is sharing loot or bounty",
        ({ } loot, null) => $"loot {ActivityFleetMemberViewModel.Isk(loot)} · bounty not shared",
        (null, { } bounty) => $"loot not shared · bounty {ActivityFleetMemberViewModel.Isk(bounty)}",
        ({ } loot, { } bounty) =>
            $"loot {ActivityFleetMemberViewModel.Isk(loot)} · bounty {ActivityFleetMemberViewModel.Isk(bounty)}"
    };

    public bool IsFleetTotalShown => FleetMembers.Count > 0;

    // Never "solo": nothing here can observe the absence of a fleet, only the presence of one. Without any the section
    // is hidden (IsShown) and this line is not on screen at all.
    public override void RefreshSummary() =>
        HeaderSummary = Context.FleetMemberCount > 1 ? Context.FleetStatusText : "no fleet has reported in";

    protected override void OnContextChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(IRunWindowContext.IsFleetShown):
                OnPropertyChanged(nameof(IsShown));
                break;
            case nameof(IRunWindowContext.RunType):
                OnPropertyChanged(nameof(PayoutExpectationLabel));
                break;
            case nameof(IRunWindowContext.FleetMembers):
                OnPropertyChanged(nameof(FleetBasisText));
                OnPropertyChanged(nameof(FleetTotalText));
                OnPropertyChanged(nameof(IsFleetTotalShown));
                break;
        }
    }

    private decimal? _FleetSum(Func<ActivityFleetMemberViewModel, decimal?> figure) =>
        FleetMembers.Select(figure).OfType<decimal>().ToList() is { Count: > 0 } shared ? shared.Sum() : null;
}
