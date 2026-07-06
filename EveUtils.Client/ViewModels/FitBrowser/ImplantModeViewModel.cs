namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One entry in the fit-detail implant-source picker: the fit's own implants, or a specific
/// character's actual implants. Mirrors <see cref="SkillModeViewModel"/>.</summary>
public sealed class ImplantModeViewModel
{
    public int? CharacterId { get; }   // null = the fit's own implants (no character)
    public string Label { get; }

    public ImplantModeViewModel(int? characterId, string label)
    {
        CharacterId = characterId;
        Label = label;
    }
}
