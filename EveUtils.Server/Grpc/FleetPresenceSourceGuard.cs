using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Server.Grpc;

/// <summary>
/// One character, one source of fleet metrics (ET-440). A character can be coupled on two machines at once — the one
/// playing it and one that merely has it signed in — and both publish for it every second. The idle one honestly says
/// "not in game", the playing one says "in game, Amarr", and every receiver used to flip between the two once a tick.
///
/// The connection that last reported the character <see cref="PresenceState.InGame"/> holds it for
/// <see cref="HoldFor"/>; any other connection's samples for that character in that fleet are dropped meanwhile. Only
/// the relay can decide this: it is the one place that knows which connection a sample came from, so receivers of
/// every version stop flickering once their server runs this. A client too old to send presence never claims, so it
/// is relayed exactly as before.
/// </summary>
public sealed class FleetPresenceSourceGuard
{
    /// <summary>Ten one-second ticks: one late tick does not hand the character over, and a closed EVE client hands it
    /// to the remaining source well inside <see cref="FleetMemberPresence.SilentAfter"/>.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(10);

    private readonly Dictionary<(long FleetId, int CharacterId), (string Source, DateTimeOffset At)> _holders = [];
    private readonly Lock _gate = new();

    /// <summary>Whether a sample from <paramref name="sourceKey"/> may be relayed.</summary>
    public bool Admit(string sourceKey, MetricSample sample, DateTimeOffset now)
    {
        var key = (sample.FleetId, sample.CharacterId);
        lock (_gate)
        {
            if (sample.Kind is MetricKind.Presence && (PresenceState)(int)sample.Value is PresenceState.InGame)
            {
                _holders[key] = (sourceKey, now);
                return true;
            }

            if (!_holders.TryGetValue(key, out var holder))
                return true;

            if (now - holder.At >= HoldFor)
            {
                _holders.Remove(key);
                return true;
            }

            return holder.Source == sourceKey;
        }
    }
}
