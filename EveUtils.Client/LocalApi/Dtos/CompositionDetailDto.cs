using System.Collections.Generic;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>A whole composition: its header plus the role-groups and their fit-entries (the doctrine tree).</summary>
public sealed record CompositionDetailDto(
    long Id,
    string Name,
    string? Description,
    string Scope,
    string? ServerAddress,
    string? ServerName,
    int OwnerCharacterId,
    string OwnerName,
    IReadOnlyList<CompositionRoleDto> Roles);
