using System;

namespace EveUtils.Client.Esi;

/// <summary>
/// Watches a character's ESI location for the length of one abyssal run. A seam so the gamelog side can be tested
/// without ESI, and so the gamelog does not have to know how a run ends.
/// </summary>
public interface IAbyssalLocationMonitor
{
    /// <summary>Starts watching until the character is seen outside the abyss. Idempotent per character.</summary>
    void Start(int characterId, Action<DateTime?> onRunEnded);

    void Stop(int characterId);
}
