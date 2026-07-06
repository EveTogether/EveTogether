using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// A dogma attribute the engine adds on top of the SDE (custom id, by convention &gt; 45000 — CCP reserves no range).
/// Used as an intermediate for formulas that the SDE leaves to the client (e.g. the propulsion <c>velocityBoost</c>).
/// </summary>
public sealed record SyntheticAttribute(
    int AttributeId, double DefaultValue, bool Stackable, bool HighIsGood, int? MaxAttributeId = null)
{
    public DogmaAttributeMeta ToMeta() => new(AttributeId, DefaultValue, Stackable, HighIsGood, MaxAttributeId);
}
