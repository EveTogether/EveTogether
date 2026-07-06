using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Messaging;

namespace EveUtils.Client.UiTests;

/// <summary>
/// An <see cref="IRemoteBusConnector"/> double with no real network: a test drives the per-server connection state by
/// hand via <see cref="RaiseStateChanged"/> to exercise code that reacts to a server reaching Connected.
/// </summary>
public sealed class FakeRemoteBusConnector : IRemoteBusConnector
{
    private readonly Dictionary<string, ServerConnectionState> _states = new();

    public Task AttachAsync(string serverAddress, int? preferredCharacterId = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DetachAsync(string serverAddress, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public IReadOnlyDictionary<string, ServerConnectionState> States => _states;

    public ServerConnectionState StateFor(string serverAddress) =>
        _states.TryGetValue(serverAddress, out var state) ? state : ServerConnectionState.Disconnected;

    public event Action<string, ServerConnectionState> StateChanged = (_, _) => { };

    public void RaiseStateChanged(string serverAddress, ServerConnectionState state)
    {
        _states[serverAddress] = state;
        StateChanged(serverAddress, state);
    }
}
