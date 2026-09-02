namespace EveUtils.Server.Api.Dtos;

/// <summary>Public ESI identity only: synced characters hold ESI tokens, which never cross this API boundary.</summary>
public sealed record ApiCharacter(int Id, string Name);
