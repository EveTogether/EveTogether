using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Commands;

/// <summary>
/// Stores what one activity yielded for the server's earnings statistic, or replaces the outcome its sharer stored
/// before with a newer revision (ET-514). One outcome per activity: when a fleet mate shared it first, theirs stays.
/// <paramref name="ActivityId"/> and <paramref name="ContributorBucket"/> are the server's keyed pseudonyms, never the
/// group code or the character id themselves. Refused with <c>PERMISSION_DENIED</c> while the server's switch is off.
/// </summary>
public sealed record ShareRunOutcomeCommand(string ActivityId, string ContributorBucket, long Revision, RunOutcomeShareDto Outcome)
    : ICommand<Result<RunOutcomeShareStatus>>;
