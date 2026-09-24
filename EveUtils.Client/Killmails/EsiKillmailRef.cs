using System.Text.Json.Serialization;

namespace EveUtils.Client.Killmails;

/// <summary>One entry of <c>GET /characters/{id}/killmails/recent/</c>: the id and hash that address the full killmail.</summary>
public sealed class EsiKillmailRef
{
    [JsonPropertyName("killmail_id")] public int KillmailId { get; set; }
    [JsonPropertyName("killmail_hash")] public string KillmailHash { get; set; } = "";
}
