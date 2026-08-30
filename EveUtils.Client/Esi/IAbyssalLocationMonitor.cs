using System;

namespace EveUtils.Client.Esi;

/// <summary>
/// Watches a character's ESI location for as long as the app runs. A seam so the gamelog side can be tested without
/// ESI, and so the gamelog does not have to know how abyssal space is recognised.
///
/// This is the app's only ESI location call. Two readers live off it: the abyssal countdown it was built for
/// (ET-62) and the location bootstrap that fills a system the gamelog has not named yet (ET-63). Adding a second
/// poll for the latter would ask ESI the same question twice, so it reads these readings instead.
/// </summary>
public interface IAbyssalLocationMonitor
{
    /// <summary>
    /// Starts watching. <paramref name="onReading"/> is called after every reading — see
    /// <see cref="EsiLocationReading"/> for what one carries. Idempotent per character.
    /// </summary>
    void Watch(int characterId, Action<EsiLocationReading> onReading);

    /// <summary>
    /// The UI thread exists, so polling (and the toast a missing scope raises) is safe. Watches asked for during
    /// start-up wait for this: raising a toast before Avalonia owns its dispatcher kills the app's own start-up.
    /// </summary>
    void UiReady();

    void Stop(int characterId);
}
