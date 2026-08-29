namespace EveUtils.Client.Dialogs;

/// <summary>
/// A module view-model whose contents can go stale while its tab/window stands open, and that therefore wants to be
/// told when the user opens it again. <see cref="ModuleHostService"/> re-selects an already-open module instead of
/// building a second one, so without this seam the second OPEN hands back the state the first one was built with —
/// which is how a fleet member who joined after the metrics screen opened stayed invisible on it (ET-46).
/// </summary>
public interface IRefreshableModule
{
    /// <summary>Re-read whatever this module snapshotted when it was built. Called on the UI thread, fire-and-forget:
    /// a module that has nothing to re-read simply does nothing.</summary>
    void RefreshModule();
}
