using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Shared.Modules.Fittings.Queries;

/// <summary>Returns all fits that have been shared to the server.</summary>
public sealed record GetSharedFittingsQuery : IQuery<IReadOnlyList<SharedFit>>;
