using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>Payload of a composition change, so every open composition window refreshes live. A change to a role or
/// entry names the composition that owns it.</summary>
/// <param name="IsClientOnly">True when the composition lives only in a client's local library.</param>
public sealed record CompositionChangePayload(
    long CompositionId,
    CompositionChangeKind Kind,
    bool IsClientOnly);
