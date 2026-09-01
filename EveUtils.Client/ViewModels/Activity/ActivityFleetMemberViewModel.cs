using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// One member of the fleet as this window can see them (ET-98): the id every location sample carries, the name once
/// public ESI resolves it, and where that sample said they were. Nothing more — a member who never sends a sample is
/// not here at all, which is the whole reason the list is captioned with what it is counted from.
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
}
