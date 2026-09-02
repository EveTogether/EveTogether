namespace EveUtils.Server.Api.Dtos;

/// <summary>Aggregated character metrics; <c>MinedJson</c> holds ore totals, not raw logs or credentials.</summary>
public sealed record ApiCharacterMetric(
    int CharacterId,
    string CharacterName,
    long BountyTotal,
    int Kills,
    string MinedJson);
