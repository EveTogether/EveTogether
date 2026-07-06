using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// A single NPC search result in the NPC picker: wraps an <see cref="NpcEnemy"/> with its resolved
/// damage profile so the list can show the dominant damage types as a hint.
/// </summary>
public sealed class NpcEnemyViewModel
{
    public int TypeId   { get; }
    public string Name  { get; }
    public string Group { get; }
    public DamageProfile Profile { get; }
    public string DamageHint { get; }

    public NpcEnemyViewModel(NpcEnemy enemy, DamageProfile profile)
    {
        TypeId   = enemy.TypeId;
        Name     = enemy.Name;
        Group    = enemy.GroupName;
        Profile  = profile;

        // Short label: "Th 46 / Kin 54" for the most dominant types.
        var parts = new List<(double Weight, string Label)>
        {
            (profile.Em,  "EM"),
            (profile.Th,  "Th"),
            (profile.Kin, "Kin"),
            (profile.Exp, "Exp"),
        }
        .Where(x => x.Weight > 0.05)
        .OrderByDescending(x => x.Weight)
        .Take(3)
        .Select(x => $"{x.Label} {x.Weight * 100:0}")
        .ToList();
        DamageHint = parts.Count > 0 ? string.Join(" / ", parts) : "—";
    }
}
