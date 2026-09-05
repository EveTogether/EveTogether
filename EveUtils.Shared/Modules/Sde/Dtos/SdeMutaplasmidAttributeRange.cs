namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>A mutaplasmid's roll-range multiplier for one dogma attribute (ET-146 deel D), read back verbatim
/// from dynamicItemAttributes.jsonl. <see cref="Min"/>/<see cref="Max"/> are multipliers on the source type's
/// base value, not rolled values themselves.</summary>
public sealed record SdeMutaplasmidAttributeRange(int AttributeId, double Min, double Max);
