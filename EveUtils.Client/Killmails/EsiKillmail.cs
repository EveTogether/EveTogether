using System;
using System.Text.Json.Serialization;

namespace EveUtils.Client.Killmails;

/// <summary>A full killmail from <c>GET /killmails/{id}/{hash}/</c>.</summary>
public sealed class EsiKillmail
{
    [JsonPropertyName("killmail_id")] public int KillmailId { get; set; }
    [JsonPropertyName("killmail_time")] public DateTimeOffset KillmailTime { get; set; }
    [JsonPropertyName("solar_system_id")] public int SolarSystemId { get; set; }
    [JsonPropertyName("victim")] public EsiKillmailVictim Victim { get; set; } = new();
    [JsonPropertyName("attackers")] public EsiKillmailAttacker[] Attackers { get; set; } = [];
}
