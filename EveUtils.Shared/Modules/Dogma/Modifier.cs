namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// A single resolved modifier registered on a target attribute during pass 2. The magnitude is not stored — it is
/// read from <see cref="Source"/>'s <see cref="SourceAttributeId"/> at evaluation time (the SDE carries no per-
/// modifier value). <see cref="Penalize"/> is the pre-computed stacking-penalty flag (multiplicative operator AND
/// non-stackable target attribute AND non-exempt source category).
/// </summary>
public readonly record struct Modifier(EffectOperator Operator, DogmaItem Source, int SourceAttributeId, bool Penalize);
