using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// In-memory <see cref="IActiveFleetState"/>. One client may drive several local characters in a
/// single active fleet, so the state is one fleet id plus an ordered set of active character ids. Reads/writes are
/// short and lock-free contention is irrelevant at ~1 Hz, but the lock keeps the (fleet, character-set) pair
/// atomic so the publisher never sees a torn snapshot mid-flush.
/// </summary>
public sealed class ActiveFleetState : IActiveFleetState, ISingletonService
{
    private readonly object _gate = new();
    private long? _fleetId;
    private bool _clientOnly;
    private string? _serverAddress;

    // Insertion-ordered so CharacterId reports the most recently entered character (back-compat with the
    // single-character callers); a HashSet semantics on the keys keeps Enter idempotent per character.
    private readonly List<int> _characterIds = [];

    public long? ActiveFleetId
    {
        get { lock (_gate) return _fleetId; }
    }

    public int? CharacterId
    {
        get { lock (_gate) return _characterIds.Count > 0 ? _characterIds[^1] : null; }
    }

    public IReadOnlyCollection<int> ActiveCharacterIds
    {
        get { lock (_gate) return _characterIds.ToArray(); }
    }

    public bool IsActiveFleetClientOnly
    {
        get { lock (_gate) return _fleetId is not null && _clientOnly; }
    }

    public string? ActiveServerAddress
    {
        get { lock (_gate) return _serverAddress; }
    }

    public void Enter(long fleetId, int characterId, string? serverAddress = null, bool clientOnly = false)
    {
        lock (_gate)
        {
            if (_fleetId != fleetId)
            {
                _fleetId = fleetId;
                _clientOnly = clientOnly; // the marker travels with the fleet, set on (re)entering it.
                _serverAddress = serverAddress; // the server (null for client-only) travels with the fleet too.
                _characterIds.Clear(); // a different fleet replaces the previous fleet and its whole active set.
            }

            if (!_characterIds.Contains(characterId))
                _characterIds.Add(characterId);
        }
    }

    public void Leave()
    {
        lock (_gate)
        {
            _fleetId = null;
            _clientOnly = false;
            _serverAddress = null;
            _characterIds.Clear();
        }
    }

    public void Leave(int characterId)
    {
        lock (_gate)
        {
            _characterIds.Remove(characterId);
            if (_characterIds.Count == 0)
            {
                _fleetId = null; // the last character leaving ends participation.
                _clientOnly = false;
                _serverAddress = null;
            }
        }
    }
}
