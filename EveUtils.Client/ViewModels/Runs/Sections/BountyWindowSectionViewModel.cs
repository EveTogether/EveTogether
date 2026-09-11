using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>BOUNTY in the run window: what the on-screen character's gamelog has paid out on this run so far.</summary>
public sealed class BountyWindowSectionViewModel(IRunWindowContext context)
    : RunWindowSection(context, RunSectionId.Bounty, "BOUNTY")
{
    public string BountyText => Context.IsInsideAbyssal
        ? "— no bounty in abyssal space"
        : Context.BountyIsk > 0 ? $"{IskFormat.Whole(Context.BountyIsk)} — own character" : "no payouts yet — own character";

    public override void RefreshSummary() => HeaderSummary = BountyText;

    protected override void OnContextChanged(string? propertyName)
    {
        if (propertyName is nameof(IRunWindowContext.BountyIsk) or nameof(IRunWindowContext.IsInsideAbyssal))
            OnPropertyChanged(nameof(BountyText));
    }
}
