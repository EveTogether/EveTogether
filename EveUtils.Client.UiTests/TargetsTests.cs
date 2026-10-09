using System.Linq;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Storage;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class TargetsTests
{
    // A spec is name/kinds/ehp; "@Name" goes through the real row builder, which knows the Tyrannos table and "?".
    private static TargetRow _Row(string spec)
    {
        if (spec.StartsWith('@'))
        {
            return TargetsWindowSectionViewModel.RowFor(new TargetSighting(null, spec[1..], null), null)!;
        }

        string[] part = spec.Split('/');
        return new TargetRow(part[0],
            [.. part[1].Split(',', System.StringSplitOptions.RemoveEmptyEntries).Select(kind => new TargetEwar(System.Enum.Parse<NpcEwarKind>(kind), 5000, false))],
            part[2].Length == 0 ? null : double.Parse(part[2]), 150, true);
    }

    /// <summary>ET-369 AC1 and AC3: stops you, slows you, repairs, damage; the lowest EHP first inside a bucket; the
    /// table's Scylla Tyrannos shows a dashed scram and a name nobody knows goes last as "?".</summary>
    [Theory]
    [InlineData("Entangler/Web/5000;Dmg//3000;Spearfisher/Scram/9000", "Spearfisher,Entangler,Dmg")]
    [InlineData("Big/Scram/9000;Small/Neut/2000;Rep/RemoteRepair/1000;Web/Web/9000", "Small,Big,Web,Rep")]
    [InlineData("@Mystery;Dmg//3000;@Scylla Tyrannos;Neut/Neut/4000", "Neut,Scylla Tyrannos,Dmg,Mystery")]
    public void Order_PutsWhatStopsYouFirst_ThenEhp_AndUnknownLast(string specs, string expected)
    {
        var ordered = TargetOrdering.Order(specs.Split(';').Select(_Row));

        Assert.Equal(expected, string.Join(",", ordered.Select(row => row.Name)));
        Assert.All(ordered.Where(row => row.Name == "Scylla Tyrannos"),
            row => Assert.Equal(["SCRAM log"], row.Ewar.Select(ewar => ewar.Text)));
        Assert.All(ordered.Where(row => row.Name == "Mystery"), row => Assert.Equal("?", row.EhpText));
    }

    /// <summary>ET-369 AC2: a type seen in two rooms stands in both, a type seen in room 2 only is not in room 1,
    /// and a run without rooms is one group.</summary>
    [Fact]
    public void GroupByRoom_PutsEachTypeInTheRoomsItWasSeenIn()
    {
        static TargetRow? ToRow(TargetSighting sighting) => new(sighting.Name, [], 1000, 100, true);

        var rooms = TargetsWindowSectionViewModel.GroupByRoom(
            [new(1, "A", 1), new(1, "B", 2), new(2, "A", 1), new(2, "C", 3)], ToRow);
        var flat = TargetsWindowSectionViewModel.GroupByRoom([new(null, "A", 1), new(null, "B", 2)], ToRow);

        Assert.Equal(["ROOM 2", "ROOM 1"], rooms.Select(room => room.Title));
        Assert.Equal(["A", "C"], rooms[0].Rows.Select(row => row.Name).Order());
        Assert.Equal(["A", "B"], rooms[1].Rows.Select(row => row.Name).Order());
        Assert.Single(flat);
        Assert.Null(flat[0].Title);
    }

    /// <summary>A rat the SDE has no EHP for shows the damage the pilot's log saw instead of "?": "observed" once the room's
    /// fight moved on without it, nothing from an incoming line, and not another room's hits.</summary>
    [Theory]
    [InlineData("1/Karybdis Tyrannos/0/500/O;1/Karybdis Tyrannos/5/700/O;1/Other/30/10/O", "1.2k observed", "dealt 1.2k · peak 120 dps")]
    [InlineData("1/Karybdis Tyrannos/0/500/O;1/Karybdis Tyrannos/5/700/O;1/Other/8/10/O", "1.2k", "dealt 1.2k · peak 120 dps")]
    [InlineData("1/Karybdis Tyrannos/0/500/O;1/Karybdis Tyrannos/5/9999/I;2/Other/90/10/O", "500", "dealt 500 · peak 50 dps")]
    [InlineData("1/Other/0/500/O", "?", "")]
    public void Damage_ReplacesTheQuestionMarkOfAnEnemyTheSdeLacks(string hits, string ehpText, string damageText)
    {
        System.DateTime start = new(2026, 10, 9, 20, 0, 0, System.DateTimeKind.Utc);
        var damage = TargetsWindowSectionViewModel.DamageByTarget(hits.Split(';').Select(spec => spec.Split('/')).Select(part =>
            ((int?)int.Parse(part[0]), new CombatEvent(start.AddSeconds(int.Parse(part[2])),
                part[4] == "O" ? DamageDirection.Outgoing : DamageDirection.Incoming, int.Parse(part[3]), part[1], null, HitQuality.Hits))));

        TargetRow row = TargetsWindowSectionViewModel.RowFor(new TargetSighting(1, "Karybdis Tyrannos", null), null)! with
        {
            Damage = damage.GetValueOrDefault((1, "Karybdis Tyrannos"))
        };

        Assert.Equal(ehpText, row.EhpText);
        Assert.Equal(damageText, row.DamageText);
    }

    /// <summary>ET-369 AC4: only an abyssal run has the TARGETS section; no other run type claims it.</summary>
    [Fact]
    public void WindowSections_OnlyAnAbyssalRunHasTargets()
    {
        Assert.Contains(RunSectionId.Targets, RunTypeCatalogue.For(RunTypeId.Abyssal).WindowSections);
        Assert.DoesNotContain(System.Enum.GetValues<RunTypeId>().Where(id => id != RunTypeId.Abyssal),
            id => RunTypeCatalogue.For(id).WindowSections.Contains(RunSectionId.Targets));
    }
}
