using System;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.FitBrowser;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>Opens the SKILLS module on a character's PLANS tab with From a fit showing (decision D1) — the one way the
/// fit detail's SKILL IMPACT… reaches the screen, whether SKILLS is already open or not.</summary>
public static class SkillsLauncher
{
    public static async Task OpenFromFitAsync(IServiceProvider services, IDialogService dialogs, int characterId,
        SkillImpactViewModel impact)
    {
        var fresh = new SkillsWindowViewModel(services, characterId);
        fresh.OpenOnFromFit(characterId, impact);
        var shown = dialogs.ShowSkills(fresh);
        if (!ReferenceEquals(shown, fresh))
        {
            shown.OpenOnFromFit(characterId, impact);
            await shown.GoToCharacterAsync(characterId);
        }
    }
}
