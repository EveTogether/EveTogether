using System.Threading.Tasks;

namespace EveUtils.Client.Esi;

/// <summary>
/// Turns a solar-system id into a system name. ESI answers a character's location with an id and nothing else in
/// the app can resolve one — the SDE import carries no universe tables — so this is the only route from
/// <c>/characters/{id}/location/</c> to a name the screens can show.
/// </summary>
public interface ISolarSystemNames
{
    /// <summary>
    /// The system's name, or null when it could not be resolved. Never throws: a caller filling a gap it could
    /// live without should not have to guard the call.
    ///
    /// No cancellation token on purpose. Everyone asking for the same system at the same moment shares one
    /// lookup, so no single caller owns it, and every caller today is a background fill nobody waits on.
    /// </summary>
    Task<string?> NameAsync(int solarSystemId);
}
