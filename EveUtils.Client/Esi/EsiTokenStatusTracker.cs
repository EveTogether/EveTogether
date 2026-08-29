using System.Collections.Concurrent;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Events;

namespace EveUtils.Client.Esi;

/// <summary>
/// The one place that knows whether a character's ESI session actually works, keyed by ESI character id.
///
/// <para>
/// Before ET-24 the answer was derived per list row from two unrelated booleans — "a token file exists"
/// plus a re-auth flag that was only ever set once, during the startup check. Every rebuild of the
/// character list threw those rows away and built fresh ones on the defaults, so a single successful
/// background refresh (which writes to the registry, which raises <c>RegistryChanged</c>, which rebuilds
/// the list) put every expired character back on a green chip. Keeping the status here, outside the rows
/// and keyed by character, makes that impossible: a rebuild re-seeds from this tracker.
/// </para>
/// <para>
/// Fed by <see cref="ClientTokenRefreshService.EnsureValidAsync"/> — which runs from the 60 s background
/// loop and from every ESI call — and by a fresh sign-in, so the status follows a real token check rather
/// than a one-off measurement at startup. <see cref="Changed"/> is the in-process signal the UI binds to;
/// <see cref="TokenRefreshedEvent"/> / <see cref="TokenRefreshFailedEvent"/> are the same news on the
/// event bus for other services (the contract <c>ClientTokenRefreshService</c> has documented all along).
/// </para>
/// </summary>
public sealed class EsiTokenStatusTracker(IEventBus bus)
{
    private readonly ConcurrentDictionary<int, TokenStatus> _statuses = new();

    /// <summary>Raised when a character's status actually changes (a repeat of the same status is silent).</summary>
    public event Action<int, TokenStatus>? Changed;

    /// <summary>The last measured status for this character, or null when it has never been checked.</summary>
    public TokenStatus? Get(int characterId) =>
        _statuses.TryGetValue(characterId, out var status) ? status : null;

    /// <summary>
    /// Records the outcome of a token check. A no-change record is stored but raises nothing, so the 60 s
    /// loop re-confirming six valid tokens does not churn the UI.
    /// </summary>
    public async Task RecordAsync(int characterId, TokenStatus status, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) return; // local-only gamelog rows have no ESI identity to track

        TokenStatus? previous = _statuses.TryGetValue(characterId, out var existing) ? existing : null;
        _statuses[characterId] = status;
        if (previous == status) return;

        Changed?.Invoke(characterId, status);

        var change = new TokenStatusChange(characterId, status);
        IIntegrationEvent published = status is TokenStatus.Valid or TokenStatus.Refreshed
            ? new TokenRefreshedEvent(change)
            : new TokenRefreshFailedEvent(change);
        await bus.PublishAsync(published, EventTarget.Local, cancellationToken);
    }

    /// <summary>Drops a character's status — used when the character itself is gone, so a later
    /// sign-in under the same id starts from a real check rather than from a stale verdict.</summary>
    public void Forget(int characterId) => _statuses.TryRemove(characterId, out _);
}
