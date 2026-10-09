using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The open escalations a plain saved site run may have flown (ET-453): same site, same character
/// and the same system — a run saved without a system does not rule one out (ET-489). Empty for an escalation run, which ticks its own escalation off at SAVE.</summary>
public sealed record FindMatchingEscalationsQuery(Guid RunId) : IQuery<Result<IReadOnlyList<OpenEscalationDto>>>;
