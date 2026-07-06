using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The per-fleet "what do I share with THIS fleet" dialog. Shows each of my characters in the fleet with a three-way
/// override per metric (use global default / share / don't share), plus an "apply to all my characters" shortcut.
/// Overrides are stored per (fleet, character, metric); absent = follow the global default (effective = override ?? global).
/// </summary>
public sealed partial class FleetShareViewModel : ObservableObject
{
    // The shareable toggles. All live combat lines collapse to one row (they map to the same combat override key);
    // location stays its own opt-in. A future non-combat metric is one entry here.
    private static readonly (MetricKind Kind, string Label)[] Shareable =
    [
        (MetricKind.Dps, "Live combat data (DPS, neut, cap)"),
        (MetricKind.Bounty, "Bounty earnings"),
        (MetricKind.Location, "Location"),
    ];

    public FleetShareViewModel(string fleetName, long fleetId, IReadOnlyList<(int Id, string Name)> myCharacters, MetricShareSnapshot current)
    {
        FleetName = fleetName;
        FleetId = fleetId;

        // The "all characters" set seeds from the first character's current overrides (or Inherit if none).
        var seedId = myCharacters.Count > 0 ? myCharacters[0].Id : 0;
        AllCharacters = BuildCharacter(seedId, "All my characters in this fleet", fleetId, current);

        foreach (var character in myCharacters)
            Characters.Add(BuildCharacter(character.Id, character.Name, fleetId, current));
    }

    public string FleetName { get; }
    public long FleetId { get; }

    /// <summary>Default on: one set of choices applied to every one of my characters in the fleet.</summary>
    [ObservableProperty] private bool _applyToAll = true;

    public FleetShareCharacterViewModel AllCharacters { get; }
    public ObservableCollection<FleetShareCharacterViewModel> Characters { get; } = [];

    /// <summary>The override writes to persist: (characterId, metric setting key, value). Value "" = inherit (remove override).</summary>
    public IReadOnlyList<(int CharacterId, string Key, string Value)> BuildOverrides()
    {
        var writes = new List<(int, string, string)>();

        if (ApplyToAll)
        {
            foreach (var character in Characters)
                foreach (var row in AllCharacters.Metrics)
                    writes.Add((character.CharacterId, MetricShareSnapshot.OverrideKeyFor(FleetId, character.CharacterId, row.Kind), ValueFor(row.ChoiceIndex)));
        }
        else
        {
            foreach (var character in Characters)
                foreach (var row in character.Metrics)
                    writes.Add((character.CharacterId, MetricShareSnapshot.OverrideKeyFor(FleetId, character.CharacterId, row.Kind), ValueFor(row.ChoiceIndex)));
        }

        return writes;
    }

    private static FleetShareCharacterViewModel BuildCharacter(int characterId, string name, long fleetId, MetricShareSnapshot current)
    {
        var rows = Shareable
            .Select(m => new FleetMetricShareRowViewModel(m.Kind, m.Label, current.OverrideChoiceIndex(fleetId, characterId, m.Kind)))
            .ToList();
        return new FleetShareCharacterViewModel(characterId, name, rows);
    }

    private static string ValueFor(int choiceIndex) => choiceIndex switch
    {
        1 => "true",   // share
        2 => "false",  // don't share
        _ => "",        // inherit → no override
    };
}
