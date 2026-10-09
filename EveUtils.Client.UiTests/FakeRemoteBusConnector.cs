using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Messaging;

namespace EveUtils.Client.UiTests;

/// <summary>
/// An <see cref="IRemoteBusConnector"/> double with no real network: a test drives the connection state by hand to
/// exercise code that reacts to it. Matching the real connector — <see cref="RaiseStateChanged"/> for the per-server
/// roll-up, <see cref="RaiseCharacterStateChanged"/> for one character's own connection (what the per-character link
/// indicators follow), and <see cref="RaiseCouplingChanged"/>, which the real connector raises when a connection reaches
/// Connected or a server is attached/detached.
/// </summary>
public sealed class FakeRemoteBusConnector : IRemoteBusConnector
{
    private readonly Dictionary<string, ServerConnectionState> _states = new();
    private readonly Dictionary<(string Server, int Character), ServerConnectionState> _characterStates = new();

    public Task AttachAsync(string serverAddress, int? preferredCharacterId = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DetachAsync(string serverAddress, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public IReadOnlyDictionary<string, ServerConnectionState> States => _states;

    public ServerConnectionState StateFor(string serverAddress) =>
        _states.TryGetValue(serverAddress, out var state) ? state : ServerConnectionState.Disconnected;

    public ServerConnectionState StateFor(string serverAddress, int characterId) =>
        _characterStates.TryGetValue((serverAddress, characterId), out var state)
            ? state
            : ServerConnectionState.Disconnected;

    public event Action<string, ServerConnectionState> StateChanged = (_, _) => { };
    public event Action<string, int, ServerConnectionState> CharacterStateChanged = (_, _, _) => { };
    public event Action<string> CouplingChanged = _ => { };

    public void RaiseStateChanged(string serverAddress, ServerConnectionState state)
    {
        _states[serverAddress] = state;
        StateChanged(serverAddress, state);
    }

    public void RaiseCouplingChanged(string serverAddress) => CouplingChanged(serverAddress);

    public void RaiseCharacterStateChanged(string serverAddress, int characterId, ServerConnectionState state)
    {
        _characterStates[(serverAddress, characterId)] = state;
        CharacterStateChanged(serverAddress, characterId, state);
    }
}
