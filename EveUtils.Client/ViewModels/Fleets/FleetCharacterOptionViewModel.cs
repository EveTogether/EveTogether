using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.ViewModels.Fleets;

/// <summary>One entry of the Fleets CHARACTER filter (ET-491): a face and a name, or "All characters (N)" with no face —
/// the shape of the KILLMAILS dropdown (ET-405), so the toolbar stays one line however many pilots there are.</summary>
public sealed class FleetCharacterOptionViewModel(int? characterId, string name, CharacterFaceViewModel? face)
{
    /// <summary>Null for the entry that stands for every character.</summary>
    public int? CharacterId { get; } = characterId;

    public string Name { get; } = name;

    public CharacterFaceViewModel? Face { get; } = face;

    public bool IsAll => CharacterId is null;
}
