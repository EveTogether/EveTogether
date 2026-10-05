using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.ViewModels.GameLogs;

/// <summary>One entry of the GAME LOGS character dropdown, the same shape as the KILLMAILS one (ET-405): "All
/// characters" first — no face, the default — then one entry per character.</summary>
public sealed partial class GameLogCharacterOptionViewModel(string? name, CharacterFaceViewModel? face) : ObservableObject
{
    public static GameLogCharacterOptionViewModel All() => new(null, null);

    public bool IsAll => Name is null;

    /// <summary>Null for "All characters".</summary>
    public string? Name { get; } = name;

    public string Label => Name ?? "All characters";

    public CharacterFaceViewModel? Face { get; } = face;

    [ObservableProperty] private int _count;
}
