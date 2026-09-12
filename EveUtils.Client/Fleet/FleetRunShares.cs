using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Client.Fleet;

/// <summary>
/// What every other pilot on a shared fleet run last said they share of it (ET-242), by group code and character —
/// the latest <see cref="RunShareUpdate"/> each one sent. The run window's LOOT section draws their items from it, and
/// the FLEET figures are taken down from it the moment a pilot stops offering them.
///
/// Held app-wide rather than by a window, like <see cref="FleetRunLegs"/>, so a window opened late still shows what
/// was shared before it existed. Kept for the session only; a discarded run's shares go with it.
/// </summary>
public sealed class FleetRunShares : ISingletonService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<int, RunShareUpdate>> _shares = new(StringComparer.Ordinal);
    private readonly IDisposable _shareSubscription;
    private readonly IDisposable _discardedSubscription;

    public FleetRunShares(IEventBus eventBus)
    {
        _shareSubscription = eventBus.Subscribe<FleetRunShareEvent>(integrationEvent =>
            _Note(integrationEvent.CharacterId, integrationEvent.Data));
        _discardedSubscription = eventBus.Subscribe<FleetRunDiscardedEvent>(integrationEvent =>
        {
            lock (_gate)
                _shares.Remove(integrationEvent.Data.GroupCode);
            Changed?.Invoke(integrationEvent.Data.GroupCode);
        });
    }

    /// <summary>A share under this group code arrived or went. Raised on whichever thread the bus delivered on.</summary>
    public event Action<string>? Changed;

    public void Dispose()
    {
        _shareSubscription.Dispose();
        _discardedSubscription.Dispose();
    }

    /// <summary>The latest share of every pilot heard from under <paramref name="groupCode"/>.</summary>
    public IReadOnlyList<(int CharacterId, RunShareUpdate Share)> Of(string groupCode)
    {
        lock (_gate)
            return _shares.TryGetValue(groupCode, out Dictionary<int, RunShareUpdate>? shares)
                ? [.. shares.Select(share => (share.Key, share.Value))]
                : [];
    }

    /// <summary>The latest share <paramref name="characterId"/> sent under <paramref name="groupCode"/>, or null for a
    /// pilot who never sent one — an older client, whose figures are left to the metric stream as they always were.</summary>
    public RunShareUpdate? Of(string groupCode, int characterId)
    {
        lock (_gate)
            return _shares.TryGetValue(groupCode, out Dictionary<int, RunShareUpdate>? shares)
                ? shares.GetValueOrDefault(characterId)
                : null;
    }

    // The pilot is the envelope's own character — the server stamps it from the session that sent it — never a claim
    // inside the payload. An older message arriving late never replaces a newer one.
    private void _Note(int? characterId, RunShareUpdate share)
    {
        if (characterId is not { } pilot || string.IsNullOrEmpty(share.GroupCode))
            return;

        lock (_gate)
        {
            if (!_shares.TryGetValue(share.GroupCode, out Dictionary<int, RunShareUpdate>? shares))
                _shares[share.GroupCode] = shares = [];
            if (shares.TryGetValue(pilot, out RunShareUpdate? known) && known.UnixMs > share.UnixMs)
                return;
            shares[pilot] = share;
        }

        Changed?.Invoke(share.GroupCode);
    }
}
