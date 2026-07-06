using System.Collections.Generic;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// The active fleet (the one this client is participating in): its header, the wing/squad structure and the full
/// roster. <c>Scope</c> is "local" or "server" (with <c>ServerName</c> set for a server fleet). <c>CompositionName</c>
/// is the coupled doctrine's name, null when none is coupled.
/// </summary>
public sealed record FleetDetailDto(
    long Id,
    string Name,
    string? Description,
    string Scope,
    string? ServerAddress,
    string? ServerName,
    int CreatorCharacterId,
    string State,
    string Activation,
    string Visibility,
    bool IsClientOnly,
    long? CompositionId,
    string? CompositionName,
    IReadOnlyList<FleetWingDto> Wings,
    IReadOnlyList<FleetMemberDto> Members);
