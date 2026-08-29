namespace EveUtils.Client.EveSettings;

/// <summary>
/// One writer at a time into an EVE settings profile.
///
/// From ET-60 on there are two of them: the user pressing a copy button, and the automatic sync deciding the clients
/// are closed. They can pick the same second, and each takes a backup and then overwrites files — interleaved, the
/// second backup would be a snapshot of a profile already half-rewritten by the first, which is precisely the thing a
/// backup exists not to be. Every path that writes into a profile (sync, restore, preset import) passes through here,
/// so they queue instead of racing.
///
/// Held only for the write itself, never across a dialog or anything waiting on the user.
/// </summary>
internal static class EveSettingsWriteGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IDisposable Acquire()
    {
        Gate.Wait();
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            Gate.Release();
        }
    }
}
