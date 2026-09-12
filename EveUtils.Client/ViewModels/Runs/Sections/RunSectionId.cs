namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// One section of the run window or the activity detail screen (ET-236). A run type names the sections it has in
/// <see cref="RunTypeCatalogue"/>; <see cref="RunSectionModules"/> says what each one is on each screen and in which
/// order they stand. Never stored, so a member can be added anywhere — the screen order is the registry's, not this.
/// </summary>
public enum RunSectionId
{
    Activity,
    Mission,
    Enemies,
    Fit,
    Fleet,
    Bounty,
    Loot,
    Escalation,
    Consumables,
    Mining
}
