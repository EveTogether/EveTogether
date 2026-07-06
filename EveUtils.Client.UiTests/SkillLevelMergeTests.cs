using System;
using EveUtils.Client.Skills;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The skills snapshot lags behind the last in-game session, so the skill-queue is folded in: any entry already
/// finished (finish_date in the past) counts as trained, and the effective level is the max of the two.
/// </summary>
public class SkillLevelMergeTests
{
    [Fact]
    public void Effective_FoldsFinishedQueueIntoSnapshot_IgnoringUnfinished()
    {
        var now = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new[]
        {
            new EsiSkill { SkillId = 100, TrainedSkillLevel = 3 },   // queue finished it to 4 since the snapshot
            new EsiSkill { SkillId = 200, TrainedSkillLevel = 5 },   // already maxed
        };
        var queue = new[]
        {
            new EsiSkillQueueEntry { SkillId = 100, FinishedLevel = 4, FinishDate = now.AddHours(-1) }, // finished -> counts
            new EsiSkillQueueEntry { SkillId = 200, FinishedLevel = 5, FinishDate = now.AddHours(-2) }, // not higher than snapshot
            new EsiSkillQueueEntry { SkillId = 500, FinishedLevel = 2, FinishDate = now.AddHours(-3) }, // new skill, finished -> added
            new EsiSkillQueueEntry { SkillId = 300, FinishedLevel = 4, FinishDate = now.AddHours(1) },  // still training -> ignored
            new EsiSkillQueueEntry { SkillId = 400, FinishedLevel = 1, FinishDate = null },             // not started -> ignored
        };

        var levels = SkillLevelMerge.Effective(snapshot, queue, now);

        Assert.Equal(4, levels[100]);            // snapshot 3 raised to the finished 4
        Assert.Equal(5, levels[200]);            // stays 5 (finished 5 is not higher)
        Assert.Equal(2, levels[500]);            // a skill trained entirely since the snapshot
        Assert.False(levels.ContainsKey(300));   // future finish_date ignored
        Assert.False(levels.ContainsKey(400));   // no finish_date ignored
    }
}
