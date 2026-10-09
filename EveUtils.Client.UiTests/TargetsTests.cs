using System.Linq;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
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

    /// <summary>ET-369 AC4: only an abyssal run has the TARGETS section; no other run type claims it.</summary>
    [Fact]
    public void WindowSections_OnlyAnAbyssalRunHasTargets()
    {
        Assert.Contains(RunSectionId.Targets, RunTypeCatalogue.For(RunTypeId.Abyssal).WindowSections);
        Assert.DoesNotContain(System.Enum.GetValues<RunTypeId>().Where(id => id != RunTypeId.Abyssal),
            id => RunTypeCatalogue.For(id).WindowSections.Contains(RunSectionId.Targets));
    }
}
