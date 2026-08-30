using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;

namespace EveUtils.Client.Platform;

/// <summary>
/// <see cref="ILocalCharacterPresence"/> over the two signals that already exist: the character registry says which
/// characters are ours, and <see cref="EveClientPresenceService"/>'s 5 s sweep says which of them have a client
/// running. Nothing new probes anything here — this only folds the two together and announces the result once, the
/// same shape ET-68 settled on after wiring each fact to each screen separately produced ET-46, ET-49 and ET-52.
///
/// The registry is read into a snapshot rather than queried per call: callers ask per member, on the UI thread,
/// while a fleet screen repaints.
/// </summary>
public sealed class LocalCharacterPresence : ILocalCharacterPresence, ISingletonService, IDisposable
{
    private readonly EveClientPresenceService? _presence;
    private readonly ICharacterRegistry? _registry;
    private readonly Action<EveClientEvidence>? _onEvidence;
    private readonly Action? _onRegistry;
    private readonly List<Action> _handlers = [];
    private readonly object _gate = new();

    // The registry snapshot. Ids are the reliable half; names cover a character registered before its ESI id was
    // known, which is also how the window-title evidence identifies a pilot.
    private volatile LocalCharacters _known = LocalCharacters.Empty;

    public LocalCharacterPresence(EveClientPresenceService? presence = null, ICharacterRegistry? registry = null)
    {
        _presence = presence;
        _registry = registry;

        if (presence is not null)
        {
            _onEvidence = _ => Announce();
            presence.Changed += _onEvidence;
        }

        if (registry is not null)
        {
            _onRegistry = () => _ = ReloadAsync();
            registry.RegistryChanged += _onRegistry;
            _ = ReloadAsync();
        }
    }

    public bool? IsInGame(int characterId, string? characterName)
    {
        // Both unknowns answer "no idea" rather than "offline", and deliberately so: every consequence of a false
        // here hides something (a location, a member's place in the count), so a missing collaborator must leave
        // the screen exactly as it was rather than quietly emptying it.
        if (_presence is null)
            return null;

        var known = _known;
        if (!known.Contains(characterId, characterName))
            return null;

        return _presence.Current.Matches(characterName ?? string.Empty, characterId);
    }

    public IDisposable Subscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    /// <summary>Re-reads the registry and tells everyone. Driven by <c>RegistryChanged</c>; public so a test can
    /// wait for the first load instead of racing it.</summary>
    public async Task ReloadAsync()
    {
        if (_registry is null)
            return;

        try
        {
            var characters = await _registry.GetAllAsync();
            _known = new LocalCharacters(
                new HashSet<int>(characters.Where(c => c.EsiCharacterId is > 0).Select(c => c.EsiCharacterId!.Value)),
                new HashSet<string>(characters.Select(c => c.Name), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            // The registry is not ready yet (first boot, before migration). The next RegistryChanged re-runs this,
            // and until then every character reads as "not ours", which hides nothing.
            return;
        }

        Announce();
    }

    private void Announce()
    {
        Action[] handlers;
        lock (_gate)
            handlers = [.. _handlers];
        if (handlers.Length == 0)
            return;

        // The sweep runs off the UI thread and every handler touches bound state. Handlers are snapshotted first,
        // so one subscribing or unsubscribing while this runs is safe.
        if (Dispatcher.UIThread.CheckAccess())
            _Deliver(handlers);
        else
            Dispatcher.UIThread.Post(() => _Deliver(handlers));
    }

    private static void _Deliver(Action[] handlers)
    {
        foreach (var handler in handlers)
        {
            // One screen throwing may not cost the others their update — the change reaching every open screen is
            // the whole point, so it cannot stop at the first one that stumbles.
            try { handler(); }
            catch (Exception) { /* a screen that cannot refresh is its own problem */ }
        }
    }

    public void Dispose()
    {
        if (_presence is not null && _onEvidence is not null)
            _presence.Changed -= _onEvidence;
        if (_registry is not null && _onRegistry is not null)
            _registry.RegistryChanged -= _onRegistry;
    }

    private sealed record LocalCharacters(IReadOnlySet<int> Ids, IReadOnlySet<string> Names)
    {
        public static readonly LocalCharacters Empty = new(
            new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public bool Contains(int characterId, string? characterName) =>
            (characterId > 0 && Ids.Contains(characterId)) ||
            (!string.IsNullOrWhiteSpace(characterName) && Names.Contains(characterName));
    }

    private sealed class Subscription(LocalCharacterPresence presence, Action handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            lock (presence._gate)
                presence._handlers.Remove(handler);
        }
    }
}
