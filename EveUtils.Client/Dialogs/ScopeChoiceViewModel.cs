using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.Dialogs;

/// <summary>One selectable ESI scope row in the scope-selection dialog.</summary>
public partial class ScopeChoiceViewModel(EsiScopeRequirement requirement, bool isSelected = true, bool isFixed = false) : ObservableObject
{
    public string Scope       { get; } = requirement.Scope;
    public string Feature     { get; } = requirement.Feature;
    public string Description { get; } = requirement.Description;
    public bool IsOptIn       { get; } = requirement.OptIn;

    /// <summary>Always requested (publicData): shown ticked, not to be unticked.</summary>
    public bool IsFixed       { get; } = isFixed;

    public bool IsEditable => !IsFixed;

    // Defaults to selected at sign-in (request everything); a re-auth pre-ticks only the already-granted scopes.
    [ObservableProperty] private bool _isSelected = isSelected || isFixed;
}
