using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Skills;

/// <summary>
/// Merges the ESI skills snapshot with the skill-queue into effective trained levels. The
/// snapshot lags behind the last in-game session; any queue entry already finished (finish_date in the past) counts as
/// trained. Effective level per skill = max(snapshot trained level, highest finished queue level). Pure, so it is
/// unit-tested apart from ESI.
/// </summary>
public static class SkillLevelMerge
{
    public static IReadOnlyDictionary<int, int> Effective(
        IEnumerable<EsiSkill> snapshot, IEnumerable<EsiSkillQueueEntry> queue, DateTimeOffset now)
    {
        var levels = new Dictionary<int, int>();
        foreach (var skill in snapshot)
            levels[skill.SkillId] = skill.TrainedSkillLevel;

        foreach (var entry in queue.Where(entry => entry.FinishDate is { } finish && finish < now))
            levels[entry.SkillId] = Math.Max(levels.GetValueOrDefault(entry.SkillId), entry.FinishedLevel);

        return levels;
    }
}
