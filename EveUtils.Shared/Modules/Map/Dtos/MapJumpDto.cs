using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Shared.Modules.Map.Dtos;

/// <summary>One stargate connection, both directions, between two indexes into <see cref="MapGraphDto.Systems"/>.</summary>
public sealed record MapJumpDto(int FromIndex, int ToIndex, MapJumpKind Kind);
