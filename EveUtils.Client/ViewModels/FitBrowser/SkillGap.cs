namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>A skill the fit requires but the selected character lacks at the required level:
/// <see cref="CurrentLevel"/> &lt; <see cref="RequiredLevel"/>. Drives the in-game "Skills Required" panel (a later UI pass).</summary>
public sealed record SkillGap(int SkillTypeId, int RequiredLevel, int CurrentLevel);
