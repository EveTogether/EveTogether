using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Grouping;

public static class RunGroupCodeArbiter
{
    public static Result<string> Select(ActivityKind activityKind, IReadOnlyList<RunGroupCodeCandidate> candidates)
    {
        if (candidates.Count == 0)
            return Result<string>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A group code requires a candidate.", "Runs"));

        IEnumerable<RunGroupCodeCandidate> eligible = activityKind == ActivityKind.Site
            ? candidates.Where(candidate => candidate.IsFleetCommander)
            : [];
        RunGroupCodeCandidate winner = eligible.Any()
            ? _First(eligible)
            : _First(candidates);

        return Result<string>.Success(winner.GroupCode);
    }

    private static RunGroupCodeCandidate _First(IEnumerable<RunGroupCodeCandidate> candidates) => candidates
            .OrderBy(candidate => candidate.StartedAtUtc)
            .ThenBy(candidate => candidate.GroupCode, StringComparer.Ordinal)
            .First();
}
