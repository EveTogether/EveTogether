namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>One input of a blueprint's manufacturing run, at its base quantity per run.</summary>
public sealed record SdeBlueprintMaterial(int TypeId, int Quantity);
