using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// The ship special-hold dogma attributes shown in the STORAGE panel, in display order — each a capacity in m³ (unit 9).
/// The cargo hold is NOT here: cargo capacity lives in the <c>Type.capacity</c> column (attr 38 carries no value), so the
/// view-model reads it via <c>GetCapacity</c> and lists it first. The drone bay (283) has its own DRONES panel; drone
/// bandwidth (1271, Mbit/s) and fighter tubes (2216, a count) are not m³ bays. Only bays the ship actually carries
/// (value &gt; 0) are shown. Attribute ids verified from the EVE Ref dogma-attribute dump.
/// </summary>
public static class StorageBayDefinitions
{
    public static IReadOnlyList<(int AttributeId, string Name)> All { get; } =
    [
        (1556, "Ore Hold"),
        (1557, "Gas Hold"),
        (3136, "Ice Hold"),
        (3227, "Asteroid Hold"),
        (1558, "Mineral Hold"),
        (1559, "Salvage Hold"),
        (1573, "Ammo Hold"),
        (1549, "Fuel Bay"),
        (912, "Fleet Hangar"),
        (908, "Ship Maintenance Bay"),
        (2055, "Fighter Bay"),
        (1646, "Command Center Hold"),
        (1653, "Planetary Commodities Hold"),
        (1560, "Ship Hold"),
        (1561, "Small Ship Hold"),
        (1562, "Medium Ship Hold"),
        (1563, "Large Ship Hold"),
        (1564, "Industrial Ship Hold"),
        (2657, "Booster Hold"),
        (1804, "Quafe Hold"),
        (2675, "Subsystem Hold"),
    ];
}
