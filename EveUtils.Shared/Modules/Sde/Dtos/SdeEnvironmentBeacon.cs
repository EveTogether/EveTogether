namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// A selectable environment/weather effect for the fit simulator: an "Effect Beacon" (group 920)
/// whose category-7 "system" effects modify the ship. <see cref="DisplayName"/> is the picker label (e.g. "Pulsar C1"),
/// <see cref="Category"/> the group header (e.g. "Wormhole"), and <see cref="SortOrder"/> orders the curated set so the
/// picker reads category-by-category, tier-ascending. <see cref="TypeId"/> is the beacon the engine injects.
/// </summary>
public sealed record SdeEnvironmentBeacon(int TypeId, string DisplayName, string Category, int SortOrder);
