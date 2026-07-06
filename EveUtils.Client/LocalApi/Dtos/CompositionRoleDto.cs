using System.Collections.Generic;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>A role-group within a composition with its fit-entries and an optional minimum group count.</summary>
public sealed record CompositionRoleDto(
    long Id,
    string RoleName,
    int? GroupMinCount,
    IReadOnlyList<CompositionEntryDto> Entries);
