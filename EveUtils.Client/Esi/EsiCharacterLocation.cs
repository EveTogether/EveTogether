using System.Text.Json.Serialization;

namespace EveUtils.Client.Esi;

/// <summary>
/// One character's position from <c>GET /characters/{id}/location/</c>. Only the solar system is read: station and
/// structure say where inside a system you are, which the abyssal countdown does not care about.
/// </summary>
public sealed class EsiCharacterLocation
{
    [JsonPropertyName("solar_system_id")] public int SolarSystemId { get; set; }
}
