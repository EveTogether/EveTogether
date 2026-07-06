namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// An effect carried by a type, with the SDE's <see cref="IsDefault"/> flag (the default variant for effects that
/// come in mutually exclusive sets).
/// </summary>
public sealed record DogmaTypeEffect(int EffectId, bool IsDefault);
