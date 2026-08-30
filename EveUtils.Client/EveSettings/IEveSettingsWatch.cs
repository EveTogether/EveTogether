namespace EveUtils.Client.EveSettings;

/// <summary>What moved under a screen showing EVE's settings.</summary>
public enum EveSettingsChangeKind
{
    /// <summary>The running-client picture changed: one closed, one started, someone logged in.</summary>
    Clients,

    /// <summary>Settings files were written — an automatic sync, a restore, a preset import. Timestamps on screen
    /// are stale and there is a new backup beside them.</summary>
    Sync,

    /// <summary>The set of backups changed: one was made, or removed.</summary>
    Backups
}

/// <summary>A snapshot of what is running right now, taken once and handed to every screen rather than re-probed
/// by each of them.</summary>
public sealed record EveClientPresenceSnapshot(int RunningClients, IReadOnlyList<string> InGame)
{
    public static EveClientPresenceSnapshot None { get; } = new(0, []);

    /// <summary>True while any client is up — including one parked on the login screen, which appears in no game
    /// log and still rewrites its files on exit.</summary>
    public bool AnyRunning => RunningClients > 0 || InGame.Count > 0;

    /// <summary>How many clients to speak of: the higher of the two signals, since a client can show in one and
    /// not the other.</summary>
    public int Count => Math.Max(RunningClients, InGame.Count);
}

/// <summary>One change worth telling a screen about, with everything the screen needs to show it.</summary>
public sealed record EveSettingsChange(
    EveSettingsChangeKind Kind,
    EveClientPresenceSnapshot Clients,
    AutoSyncRun? Run = null);

/// <summary>
/// The one place a change under the EVE-settings screens is announced, and the one place those screens subscribe.
///
/// It exists because the tool has something running underneath it: a sweep that watches for EVE clients, and an
/// automatic sync that writes files when they all close. Both were happening correctly while the screen went on
/// showing the state it had when it was opened — the operator watched an automatic backup being made behind a
/// banner still claiming a client was running (ET-68). Wiring each of those facts to each screen separately is
/// what produced ET-46, ET-49 and ET-52 in turn, so this is one announcement with however many listeners, the same
/// shape as <see cref="Fleet.IFleetRosterWatch"/>.
///
/// The announcement carries the state rather than a nudge to go and look: a screen updating from it does no I/O,
/// and a test drives it by announcing rather than by waiting for a timer.
/// </summary>
public interface IEveSettingsWatch
{
    /// <summary>Tell every open screen. Called after the change has actually landed, so a listener that re-reads
    /// reads the new state.</summary>
    void Announce(EveSettingsChange change);

    /// <summary>Listen. Handlers run on the UI thread; dispose to stop, which a screen does when it closes.</summary>
    IDisposable Subscribe(Action<EveSettingsChange> handler);

    /// <summary>What is running right now, probed fresh — the "check again" button and the first paint of a screen
    /// both go through this, so nothing has a second way of working out the same answer.</summary>
    EveClientPresenceSnapshot ProbeClients();
}
