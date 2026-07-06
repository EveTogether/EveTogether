using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Shared.Modules.Fittings.Queries;

/// <summary>
/// Returns locally stored fittings. <see cref="OwnerId"/> null = all fittings across every character
/// (fits are portable; the list is global, only the source character id is kept). When set,
/// filters to that owner.
/// </summary>
public sealed record GetFittingsQuery(string? OwnerId = null) : IQuery<IReadOnlyList<LocalFitting>>;
