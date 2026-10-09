namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>A saved run an open escalation can be linked to after the fact (ET-489), with what makes it a better or
/// worse candidate — what the picker shows beside the run.</summary>
public sealed record LinkableRunDto(
    Guid RunId,
    long CharacterId,
    string? CharacterNameSnapshot,
    string? SiteName,
    int? SolarSystemId,
    DateTime StartedAtUtc,
    bool IsSameCharacter,
    bool IsSameSystem);
