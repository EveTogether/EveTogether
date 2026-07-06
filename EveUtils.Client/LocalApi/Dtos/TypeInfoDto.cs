namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// A type-resolution helper row: a type id resolved to its name + group and a public CCP icon URL, so a widget
/// can render ship/module names and icons without shipping its own SDE. <c>Name</c> falls back to "type {id}" when the
/// SDE store has not been imported.
/// </summary>
public sealed record TypeInfoDto(int Id, string Name, string? GroupName, string IconUrl);
