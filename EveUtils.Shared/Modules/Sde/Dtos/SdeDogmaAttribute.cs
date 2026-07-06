namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>A dogma attribute value carried by a specific type (e.g. CPU output, powergrid, slot counts).</summary>
public sealed record SdeDogmaAttribute(int AttributeId, double Value);
