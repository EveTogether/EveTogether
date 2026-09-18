using System.Globalization;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>An <see cref="ActivitySummary"/> and its member runs' facts turned into one overview row — the Local
/// tab's read and (ET-311) a server tab's, so a row reads the same whichever store it came from.</summary>
internal static class ActivityOverviewRows
{
    /// <summary>What this machine's own characters made of an activity (ET-296) — the stored per-character split,
    /// added back up over the ones the registry still holds. A caller that named no characters asks for no split and
    /// reads the group's own total, as every screen did before this ticket; so does a summary built before the split
    /// was stored, until the startup rebuild reaches it (<c>IskContributors.Signature</c>).</summary>
    private static IskBreakdown _OwnShareOf(ActivitySummary summary, IReadOnlySet<long>? ownCharacterIds)
    {
        IskBreakdown group = StoredIskBreakdown.Read(summary.IskContributions);
        if (ownCharacterIds is null || StoredIskBreakdown.ReadByCharacter(summary.IskContributionsByCharacter) is not { } byCharacter)
            return group;

        return IskBreakdown.Sum(byCharacter
            .Where(character => ownCharacterIds.Contains(character.Key))
            .Select(character => character.Value));
    }

    /// <summary>The fleet mates who took something out of this activity — read off the same stored split the own
    /// share is, so a row whose value all sits on somebody else's run can name them rather than read unvalued.</summary>
    private static IReadOnlyList<ActivityCrewMemberDto> _OtherEarnersOf(
        ActivitySummary summary, IReadOnlySet<long>? ownCharacterIds, IReadOnlyList<ActivityCrewMemberDto> crew)
    {
        if (ownCharacterIds is null || StoredIskBreakdown.ReadByCharacter(summary.IskContributionsByCharacter) is not { } byCharacter)
            return [];

        HashSet<long> earners = [.. byCharacter
            .Where(character => !ownCharacterIds.Contains(character.Key) && character.Value.HasFigure)
            .Select(character => character.Key)];
        return [.. crew.Where(member => earners.Contains(member.CharacterId))];
    }

    public static ActivityOverviewRowDto ToDto(
        ActivitySummary summary, IEnumerable<RunParameter> rewardRows,
        IEnumerable<(long CharacterId, string? CharacterNameSnapshot)> crew, bool hasAutoSavedRun,
        IEnumerable<ActivityServerSyncDto> serverSyncStates, IReadOnlySet<long>? ownCharacterIds)
    {
        (long CharacterId, string? CharacterNameSnapshot)[] flewIt = [.. crew];
        RunParameter[] all = [.. rewardRows];
        // AbyssalFilament is the pocket's own tier and weather (ET-241), never a reward the pilot earned — read
        // separately for the row's own name, and kept out of the reward-chip list below on purpose. Its resolved
        // type id and count (ET-249) are CONSUMABLES' own cost, not a reward either.
        string? abyssalFilamentText =
            all.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament)?.TypedValue;
        // ET-260: every own toon's run under one mission group used to carry an identical copy of the same reward
        // line (fixed at the source now, and swept once for activities saved before that fix) — Distinct still
        // guards this chip against reading double for a group this repair has not reached yet.
        //
        // The escalation's own accounting keys (ET-289) never become chips of their own — a raw ESCALATIONDUNGEONID
        // or ESCALATIONSYSTEM chip names nothing a pilot earned, only bookkeeping the Escalation chip below already
        // carries the one useful fact of (the destination site, on its own TypedValue; the expiry, read separately
        // below). MissionLocation is the same kind of bookkeeping, for a mission's own detail screen only.
        RunParameter[] rewards = [.. all.Where(parameter => parameter.ParameterKey is not (
            RunParameterKey.AbyssalFilament or RunParameterKey.AbyssalFilamentTypeId or RunParameterKey.AbyssalFilamentCount
            or RunParameterKey.EscalationDungeonId or RunParameterKey.EscalationSystem or RunParameterKey.EscalationSolarSystemId
            or RunParameterKey.EscalationExpiresAtUtc or RunParameterKey.MissionLocation))
            .DistinctBy(parameter => (parameter.ParameterKey, parameter.TypedValue, parameter.Amount, parameter.BonusWindowSeconds, parameter.ObservedAtUtc))];
        DateTime? escalationExpiresAtUtc = all
            .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationExpiresAtUtc)?.TypedValue is { } expiresAt
            && DateTime.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime expiresAtUtc)
                ? expiresAtUtc
                : null;
        ActivityCrewMemberDto[] members = [.. flewIt.GroupBy(member => member.CharacterId)
            .Select(group => new ActivityCrewMemberDto(
                group.Key,
                group.Select(member => member.CharacterNameSnapshot).FirstOrDefault(name => !string.IsNullOrEmpty(name))))
            .OrderBy(member => member.CharacterId)];
        return new ActivityOverviewRowDto(
            summary.Id, summary.GroupCode, summary.RunId, summary.ActivityKind, summary.SiteName,
            summary.SignatureGroupSnapshot, summary.SiteTypeId, summary.SolarSystemId,
            summary.StartedAtUtc, summary.DurationSeconds, summary.RunsIncluded, summary.ParticipantCount,
            members,
            [.. rewards.GroupBy(reward => reward.ParameterKey)
                .Select(group => new ActivityRewardDto(group.Key, _SumOrNull(group.Select(reward => reward.Amount)),
                    group.Key == RunParameterKey.Escalation
                        ? group.Select(reward => reward.TypedValue).FirstOrDefault(value => !string.IsNullOrEmpty(value))
                        : null,
                    group.Key == RunParameterKey.Escalation ? escalationExpiresAtUtc : null))],
            summary.BountyIsk, summary.LootIskNet, summary.EnemyTypeCount,
            rewards.Any(reward => reward.ParameterKey == RunParameterKey.Escalation),
            hasAutoSavedRun,
            [.. serverSyncStates],
            StoredIskBreakdown.Read(summary.IskContributions),
            _OwnShareOf(summary, ownCharacterIds),
            ownCharacterIds is null || flewIt.Any(member => ownCharacterIds.Contains(member.CharacterId)),
            _OtherEarnersOf(summary, ownCharacterIds, members),
            abyssalFilamentText,
            _OwnShareByCharacterOf(summary, ownCharacterIds));
    }

    private static IReadOnlyDictionary<long, IskBreakdown>? _OwnShareByCharacterOf(
        ActivitySummary summary, IReadOnlySet<long>? ownCharacterIds)
    {
        if (ownCharacterIds is null || StoredIskBreakdown.ReadByCharacter(summary.IskContributionsByCharacter) is not { } byCharacter)
            return null;

        return byCharacter
            .Where(character => ownCharacterIds.Contains(character.Key))
            .ToDictionary(character => character.Key, character => character.Value);
    }

    private static decimal? _SumOrNull(IEnumerable<decimal?> amounts)
    {
        decimal[] known = [.. amounts.Where(amount => amount.HasValue).Select(amount => amount!.Value)];
        return known.Length == 0 ? null : known.Sum();
    }
}
