using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// One member of the fleet as this window can see them (ET-98): the id every sample carries, the name once public
/// ESI resolves it, where they said they were, and what their run has made. Nothing more — a member who never sends
/// a sample is not here at all, which is the whole reason the list is captioned with what it is counted from.
///
/// Location, loot and bounty are three separate opt-ins, so any of the three may be missing on a member who is
/// plainly here. FLEET says which one a member withholds, and leaves a figure nobody has off the row (ET-272).
/// </summary>
public sealed partial class ActivityFleetMemberViewModel : ObservableObject
{
    public ActivityFleetMemberViewModel(int characterId)
    {
        CharacterId = characterId;
        Name = $"Char {characterId}";
    }

    public int CharacterId { get; }

    [ObservableProperty] private string _name;

    [ObservableProperty] private string _locationText = "not sharing a system";

    /// <summary>This member's run loot, net of what it cost them, as their own client priced it. Null is a figure
    /// not heard; never 0, which would say they found nothing.</summary>
    [ObservableProperty] private decimal? _lootIsk;

    [ObservableProperty] private decimal? _bountyIsk;
}
