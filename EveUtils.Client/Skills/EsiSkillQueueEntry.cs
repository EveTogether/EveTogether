using System;
using System.Text.Json.Serialization;

namespace EveUtils.Client.Skills;

/// <summary>One entry from ESI <c>GET /characters/{id}/skillqueue/</c>. An entry whose <see cref="FinishDate"/> is in
/// the past has finished training since the skills snapshot, so it counts as trained to <see cref="FinishedLevel"/>.</summary>
public sealed class EsiSkillQueueEntry
{
    [JsonPropertyName("skill_id")] public int SkillId { get; set; }
    [JsonPropertyName("finished_level")] public int FinishedLevel { get; set; }
    [JsonPropertyName("queue_position")] public int QueuePosition { get; set; }
    [JsonPropertyName("start_date")] public DateTimeOffset? StartDate { get; set; }
    [JsonPropertyName("finish_date")] public DateTimeOffset? FinishDate { get; set; }
}
