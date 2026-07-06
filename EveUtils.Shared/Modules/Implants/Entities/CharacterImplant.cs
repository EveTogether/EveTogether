namespace EveUtils.Shared.Modules.Implants.Entities;

/// <summary>
/// One implant a character has plugged in, cached client-side after an ESI import
/// (<c>GET /characters/{id}/implants/</c>). Implants have no level — just the type. Keyed by
/// (CharacterId, ImplantTypeId). Feeds the fit-detail "character implants" source and the SP/min training rate
/// (attribute implants raise the character's effective attributes).
/// </summary>
public sealed class CharacterImplant
{
    public int CharacterId { get; set; }
    public int ImplantTypeId { get; set; }
}
