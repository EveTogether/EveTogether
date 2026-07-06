namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One entry in the fit-detail skills dropdown: an "all skills at level N" planning baseline, or a coupled
/// character whose imported skills drive the stats. The window applies the choice via its SelectedSkillMode binding.
/// </summary>
public sealed class SkillModeViewModel
{
    /// <summary>The character whose skills this mode uses, or null for an "all skills at level N" baseline.</summary>
    public int? CharacterId { get; }

    /// <summary>The uniform level 1-5 for an all-skills baseline (0 for a character mode).</summary>
    public int AllLevel { get; }
    public string Label { get; }

    public SkillModeViewModel(int? characterId, int allLevel, string label)
    {
        CharacterId = characterId;
        AllLevel = allLevel;
        Label = label;
    }
}
