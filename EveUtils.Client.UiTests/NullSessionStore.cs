using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="IClientSessionStore"/> double for headless tests that drive a background service's per-character method
/// directly (so the server-enumeration cycle, which is the only caller of the store, never runs). Returns no servers
/// and no sessions.
/// </summary>
public sealed class NullSessionStore : IClientSessionStore
{
    public Task SaveAsync(string serverAddress, ClientSessionTokens tokens, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ClientSessionTokens?> LoadAsync(string serverAddress, CancellationToken cancellationToken = default) => Task.FromResult<ClientSessionTokens?>(null);
    public Task<ClientSessionTokens?> LoadForCharacterAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default) => Task.FromResult<ClientSessionTokens?>(null);
    public Task<IReadOnlyList<ClientSessionTokens>> LoadAllAsync(string serverAddress, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClientSessionTokens>>([]);
    public Task RemoveAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListServersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> ListServersForCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
}
