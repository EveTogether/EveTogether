using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>BOUNTY in the run window: what the on-screen character's gamelog has paid out on this run so far, or —
/// in an own-toon group, with or without a fleet (ET-257) — one row per participant's own bounty, the same
/// breakdown the detail screen already gives.</summary>
public sealed class BountyWindowSectionViewModel(IRunWindowContext context)
    : RunWindowSection(context, RunSectionId.Bounty, "BOUNTY")
{
    public ObservableCollection<ActivityBountyRowViewModel> BountyRows { get; } = [];

    public bool IsGroup => Context.Participants.Count > 1;

    public string BountyText => Context.IsInsideAbyssal
        ? "— no bounty in abyssal space"
        : Context.BountyIsk > 0 ? $"{IskFormat.Whole(Context.BountyIsk)} — own character" : "no payouts yet — own character";

    public override void RefreshSummary() => HeaderSummary = BountyText;

    /// <summary>Clock-driven like the fit section is: <see cref="Runs.RunParticipantViewModel.BountyIsk"/> is
    /// refreshed elsewhere (<c>ActivityWindowViewModel._RefreshParticipantsAsync</c>), this only reads it back.</summary>
    public override void Refresh(DateTime nowUtc)
    {
        OnPropertyChanged(nameof(IsGroup));
        if (!IsGroup)
        {
            if (BountyRows.Count > 0)
                BountyRows.Clear();
            return;
        }

        List<ActivityBountyRowViewModel> rows = [.. Context.Participants
            .OrderByDescending(participant => participant.BountyIsk)
            .Select(participant => new ActivityBountyRowViewModel(
                participant.CharacterId, participant.BountyIsk, _ => participant.CharacterName))];
        if (rows.Count == BountyRows.Count
            && rows.Select(row => (row.CharacterText, row.IskText))
                .SequenceEqual(BountyRows.Select(row => (row.CharacterText, row.IskText))))
            return;

        BountyRows.Clear();
        foreach (ActivityBountyRowViewModel row in rows)
            BountyRows.Add(row);
    }

    protected override void OnContextChanged(string? propertyName)
    {
        if (propertyName is nameof(IRunWindowContext.BountyIsk) or nameof(IRunWindowContext.IsInsideAbyssal))
            OnPropertyChanged(nameof(BountyText));
    }
}
