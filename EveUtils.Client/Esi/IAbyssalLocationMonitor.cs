using System;

namespace EveUtils.Client.Esi;

/// <summary>
/// Watches a character's ESI location for as long as the app runs. A seam so the gamelog side can be tested without
/// ESI, and so the gamelog does not have to know how abyssal space is recognised.
/// </summary>
public interface IAbyssalLocationMonitor
{
    /// <summary>
    /// Starts watching. <paramref name="onPresence"/> is called after every reading: <c>true</c> = inside abyssal
    /// space, <c>false</c> = outside, <c>null</c> = the watch was lost. Idempotent per character.
    /// </summary>
    void Watch(int characterId, Action<bool?, DateTime> onPresence);

    /// <summary>
    /// The UI thread exists, so polling (and the toast a missing scope raises) is safe. Watches asked for during
    /// start-up wait for this: raising a toast before Avalonia owns its dispatcher kills the app's own start-up.
    /// </summary>
    void UiReady();

    void Stop(int characterId);
}
