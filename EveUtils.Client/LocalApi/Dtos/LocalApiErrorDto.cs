namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>Body of a refused write: a machine-readable code (e.g. <c>PRESET_READ_ONLY</c>) and a readable message.</summary>
public sealed record LocalApiErrorDto(string Code, string Message);
