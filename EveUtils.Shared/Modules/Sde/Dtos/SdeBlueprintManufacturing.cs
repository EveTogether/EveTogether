namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>What one manufacturing run of a blueprint makes and takes (ET-501): the product and the materials at their
/// base quantities before any ME reduction, and the base job time before TE.</summary>
public sealed record SdeBlueprintManufacturing(
    int BlueprintTypeId, int ProductTypeId, int ProductQuantity, int TimeSeconds, int MaxProductionLimit,
    IReadOnlyList<SdeBlueprintMaterial> Materials);
