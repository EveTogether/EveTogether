using System.Text.Json.Serialization;

namespace EveUtils.Client.Esi;

/// <summary>
/// One solar system from the public <c>GET /universe/systems/{id}/</c>. Only the name is read: this type exists
/// solely to turn the id <see cref="EsiCharacterLocation"/> reports into something a screen can show.
/// </summary>
public sealed class EsiSolarSystem
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}
