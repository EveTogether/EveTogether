using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Server.Esi;

/// <summary>
/// One lock per character around every EVE SSO refresh on the server, shared by the on-demand
/// <see cref="ServerEsiTokenProvider"/> and the background <see cref="ServerTokenRefreshService"/>. EVE rotates refresh
/// tokens, so two refreshes of one character at the same time offer the same token twice and the second gets
/// invalid_grant, which ends the grant (ET-448). Whoever enters reads the stored token again inside the lock.
/// </summary>
public sealed class ServerTokenRefreshGate : ISingletonService
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

    public async Task<IDisposable> EnterAsync(int esiCharacterId, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(esiCharacterId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Held(gate);
    }

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
