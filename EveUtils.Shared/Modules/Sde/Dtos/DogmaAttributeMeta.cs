namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// Calculation-relevant metadata of a dogma attribute. <see cref="Stackable"/> drives the stacking-penalty gate (a
/// non-stackable attribute penalises multiplicative modifiers); <see cref="HighIsGood"/> decides whether an
/// assignment modifier keeps the highest or lowest value. <see cref="DefaultValue"/> is the starting point when a
/// type does not carry the attribute itself. <see cref="MaxAttributeId"/> caps the resolved value at the value of
/// another attribute on the same item (e.g. damage resonances cap at 1.0 = 0% resist); null when uncapped.
/// </summary>
public sealed record DogmaAttributeMeta(
    int AttributeId, double DefaultValue, bool Stackable, bool HighIsGood, int? MaxAttributeId = null);
