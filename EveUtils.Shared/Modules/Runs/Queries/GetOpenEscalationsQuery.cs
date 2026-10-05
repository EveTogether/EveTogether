using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Every escalation still to be flown, across runs and characters, soonest deadline first (ET-451).</summary>
/// <param name="CharacterIds">Only escalations registered on these characters' runs — this machine's own, since an
/// escalation run can only be started for one of them. Null for every character in the store.</param>
public sealed record GetOpenEscalationsQuery(IReadOnlySet<long>? CharacterIds = null)
    : IQuery<Result<IReadOnlyList<OpenEscalationDto>>>;
