namespace EveUtils.Shared.Modules.Gamelog.Entities;

/// <summary>
/// Persisted slice of a character's session metrics: only the cumulative figures worth surviving a
/// restart — bounty total, kills and mined units per ore (JSON). DPS / hit-rate / enemies / quality stay
/// session-only by design. Keyed by the gamelog character name (client-side; the server doesn't use this).
/// </summary>
public sealed class CharacterMetricState
{
    public int Id { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public long BountyTotal { get; set; }
    public int Kills { get; set; }

    /// <summary>Mined units per ore type as a JSON object (<c>{"Veldspar":1234,…}</c>).</summary>
    public string MinedJson { get; set; } = "{}";
}
