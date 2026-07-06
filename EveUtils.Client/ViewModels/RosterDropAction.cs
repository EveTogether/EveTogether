namespace EveUtils.Client.ViewModels;

/// <summary>How a roster drag-drop resolves against the node it was dropped on (stream G / G-3): a no-op, a move to a
/// position, or a swap with the member already occupying the target commander slot.</summary>
public enum RosterDropAction
{
    None,
    Move,
    Swap
}
