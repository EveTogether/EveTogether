namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Resolves an EVE type id to a display name for the fit-browser. Backed by the SDE
/// store; falls back to <c>type {id}</c> when the store has no entry or has not been imported yet.
/// </summary>
public interface ISdeNameResolver
{
    string TypeName(int typeId);

    /// <summary>The hull/group class for a type (e.g. "Frigate", "Cruiser"), or null when the SDE has no entry — used
    /// for the hull-class label next to the ship name in the browser.</summary>
    string? GroupName(int typeId);
}
