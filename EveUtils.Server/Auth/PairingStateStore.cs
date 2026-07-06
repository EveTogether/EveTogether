using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Server.Auth;

/// <summary>In-memory, TTL-bounded pairing states. No persistence — pairings are short-lived.</summary>
public sealed class PairingStateStore : ISingletonService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, PairingState> _states = new();

    public void Add(PairingState state) => _states[state.PairingId] = state;

    public PairingState? Get(string pairingId)
    {
        if (!_states.TryGetValue(pairingId, out var state))
            return null;
        if (DateTimeOffset.UtcNow - state.CreatedAt > Ttl)
        {
            _states.TryRemove(pairingId, out _);
            return null;
        }
        return state;
    }

    /// <summary>Finds a pending pairing by its CSRF <c>oauth_state</c> (used by the server SSO callback).</summary>
    public PairingState? GetByState(string oauthState) =>
        _states.Values.FirstOrDefault(s =>
            string.Equals(s.OAuthState, oauthState, StringComparison.Ordinal)
            && DateTimeOffset.UtcNow - s.CreatedAt <= Ttl);

    public void Remove(string pairingId) => _states.TryRemove(pairingId, out _);
}
