using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels;

/// <summary>Killmail row in the per-fleet sharing dialog, with the same three-way choice as every metric row.</summary>
public sealed partial class FleetKillmailShareRowViewModel(int choiceIndex) : ObservableObject
{
    public string Label => "Killmails (kills and losses)";

    [ObservableProperty] private int _choiceIndex = choiceIndex;
}
