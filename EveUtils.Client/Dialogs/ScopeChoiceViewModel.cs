using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.Dialogs;

/// <summary>One selectable ESI scope row in the scope-selection dialog.</summary>
public partial class ScopeChoiceViewModel(EsiScopeRequirement requirement, bool isSelected = true) : ObservableObject
{
    public string Scope       { get; } = requirement.Scope;
    public string Feature     { get; } = requirement.Feature;
    public string Description { get; } = requirement.Description;

    // Defaults to selected at sign-in (request everything); a re-auth pre-ticks only the already-granted scopes.
    [ObservableProperty] private bool _isSelected = isSelected;
}
