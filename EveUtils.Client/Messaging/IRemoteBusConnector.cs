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

    /// <summary>Live connection state for one server (Disconnected if not attached).</summary>
    ServerConnectionState StateFor(string serverAddress);

    /// <summary>Raised whenever a server's connection state changes: (serverAddress, newState).</summary>
    event Action<string, ServerConnectionState> StateChanged;
}
