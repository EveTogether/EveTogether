using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>
/// Where a character's skill queue stands (ET-324), from the rows <c>EsiSkillImporter</c> stores: the skill at the head
/// of the queue, when it finishes and when the whole queue runs out. A queue with rows but no dates is paused — ESI
/// leaves them out while the character is logged off with training stopped.
/// </summary>
public sealed record SkillQueueStanding(
    string SkillName,
    int Level,
    DateTimeOffset? SkillEndsAt,
    DateTimeOffset? QueueEndsAt,
    int Queued)
{
    /// <summary>Under this the queue reads amber: a week is when a pilot wants to be told to top it up.</summary>
    public static readonly TimeSpan ShortQueue = TimeSpan.FromDays(7);

    /// <summary>The bar under TRAINING is full at this much queue — the mockup's scale.</summary>
    public static readonly TimeSpan FullBar = TimeSpan.FromDays(60);

    public bool IsPaused => SkillEndsAt is null;

    /// <summary>Null for an empty queue.</summary>
    public static SkillQueueStanding? From(IReadOnlyList<CharacterSkillQueueEntry> entries, Func<int, string> skillName)
    {
        if (entries.Count == 0)
            return null;

        CharacterSkillQueueEntry head = entries.MinBy(entry => entry.QueuePosition)!;
        return new SkillQueueStanding(skillName(head.SkillTypeId), head.FinishedLevel, head.FinishDate,
            entries.Max(entry => entry.FinishDate), entries.Count);
    }

    public string SkillText => $"{SkillName} {_Roman(Level)}";

    public TimeSpan? QueueLeft(DateTimeOffset now) => QueueEndsAt is { } end ? _NotNegative(end - now) : null;

    public bool IsShort(DateTimeOffset now) => QueueLeft(now) is { } left && left < ShortQueue;

    /// <summary>"2d 2h · queue 56d 13h", "5d 21h · queue runs dry in 5d 22h", "queue paused · 2 waiting".</summary>
    public string DetailText(DateTimeOffset now)
    {
        if (IsPaused || SkillEndsAt is not { } skillEnd || QueueLeft(now) is not { } queueLeft)
            return $"queue paused · {Queued} waiting";

        string queue = IsShort(now) ? "queue runs dry in " : "queue ";
        return $"{Until(_NotNegative(skillEnd - now))} · {queue}{Until(queueLeft)}";
    }

    public double BarFraction(DateTimeOffset now) =>
        QueueLeft(now) is { } left ? Math.Min(1, left / FullBar) : 0;

    /// <summary>"56d 13h", or "3h 07m" under a day.</summary>
    public static string Until(TimeSpan left)
    {
        int minutes = (int)Math.Round(left.TotalMinutes);
        int days = minutes / 1440;
        int hours = minutes % 1440 / 60;
        return days > 0 ? $"{days}d {hours}h" : $"{hours}h {minutes % 60:00}m";
    }

    private static TimeSpan _NotNegative(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;

    private static string _Roman(int level) => level switch
    {
        1 => "I",
        2 => "II",
        3 => "III",
        4 => "IV",
        5 => "V",
        _ => level.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
