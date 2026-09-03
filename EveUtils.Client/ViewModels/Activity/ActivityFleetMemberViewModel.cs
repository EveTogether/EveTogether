using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// One member of the fleet as this window can see them (ET-98): the id every sample carries, the name once public
/// ESI resolves it, where they said they were, and what their run has made. Nothing more — a member who never sends
/// a sample is not here at all, which is the whole reason the list is captioned with what it is counted from.
///
/// Location, loot and bounty are three separate opt-ins, so any of the three may be missing on a member who is
/// plainly here. Each says so in its own words rather than showing a zero.
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
    /// they do not share; never 0, which would say they found nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IskText))]
    private decimal? _lootIsk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IskText))]
    private decimal? _bountyIsk;

    public string IskText => (LootIsk, BountyIsk) switch
    {
        (null, null) => "not sharing loot or bounty",
        ({ } loot, null) => $"loot {Isk(loot)} · bounty not shared",
        (null, { } bounty) => $"loot not shared · bounty {Isk(bounty)}",
        ({ } loot, { } bounty) => $"loot {Isk(loot)} · bounty {Isk(bounty)}"
    };

    /// <summary>The one ISK format this window writes, the same one the payout figures beside it use.</summary>
    public static string Isk(decimal value) => $"{value:N2} ISK";
}
