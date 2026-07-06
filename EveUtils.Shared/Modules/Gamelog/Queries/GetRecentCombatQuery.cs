using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Modules.Gamelog.Dtos;

namespace EveUtils.Shared.Modules.Gamelog.Queries;

/// <summary>Reads the current owner's recent combat samples. Gated by <c>gamelog.view</c>.</summary>
[RequiresPermission(GamelogPermissions.View)]
public sealed record GetRecentCombatQuery(int Take = 20) : IQuery<IReadOnlyList<CombatSampleDto>>;
