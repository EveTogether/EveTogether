using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Messaging;

/// <summary>
/// Attaches the client to the remote event bus of one or more paired servers, with
/// auto-reconnect per server. A character can be coupled to several servers at once.
/// </summary>
public interface IRemoteBusConnector
{
    /// <summary>
    /// Starts (or restarts) the managed connect/auto-reconnect loop for <paramref name="serverAddress"/>.
    /// <paramref name="preferredCharacterId"/> picks which character's session to attach with (e.g. the
    /// one just paired); null = most recent for that server.
    /// </summary>
    Task AttachAsync(string serverAddress, int? preferredCharacterId = null, CancellationToken cancellationToken = default);

    /// <summary>Stops the connection to <paramref name="serverAddress"/> and closes its stream (decouple).</summary>
    Task DetachAsync(string serverAddress, CancellationToken cancellationToken = default);

    /// <summary>Live connection state per server address.</summary>
    IReadOnlyDictionary<string, ServerConnectionState> States { get; }

    /// <summary>Live connection state for one server (Disconnected if not attached). This is the roll-up across the
    /// characters coupled to it — "is this server usable at all" — so it is the wrong thing to paint a per-character
    /// indicator with; use the overload below for that.</summary>
    ServerConnectionState StateFor(string serverAddress);

    /// <summary>
    /// Live state of one character's own connection to a server (Disconnected if it has none).
    /// <para>Separate from the server roll-up because the two answer different questions and a character can be in
    /// trouble on a server that is otherwise perfectly healthy: when one character's session is swept and five others
    /// keep working, the roll-up is <see cref="ServerConnectionState.Connected"/> and says nothing at all about the
    /// sixth (ET-123).</para>
    /// </summary>
    ServerConnectionState StateFor(string serverAddress, int characterId);

    /// <summary>Raised whenever a server's roll-up state changes: (serverAddress, newState). For consumers that care
    /// about the server as a whole — reloading its lists, the home dashboard's summary.</summary>
    event Action<string, ServerConnectionState> StateChanged;

    /// <summary>Raised whenever ONE character's connection changes: (serverAddress, characterId, newState). What the
    /// per-character link indicators follow, so one character's trouble is neither hidden by its neighbours nor
    /// smeared across them.</summary>
    event Action<string, int, ServerConnectionState> CharacterStateChanged;
}
