using EveUtils.Shared.Modules.Sde.Storage;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>Unit tests for the ET-367 static NPC-knowledge table (AC2 faction reading, AC3 the two Tyrannos agents).
/// Pure C# logic — no SDE store involved, so nothing here is skipped.</summary>
public sealed class AbyssalNpcKnowledgeTests
{
    [Theory]
    [InlineData(new[] { "Tangling Damavik" }, AbyssalNpcFaction.Triglavian)]
    [InlineData(new[] { "Tangling Damavik", "Ephialtes Entangler" }, AbyssalNpcFaction.Mixed)]
    [InlineData(new[] { "Tangling Damavik", "Triglavian Biocombinative Cache" }, AbyssalNpcFaction.Triglavian)]
    [InlineData(new[] { "Triglavian Biocombinative Cache" }, null)]
    public void Faction_NamesAcrossFactions_ReturnsExpected(string[] names, AbyssalNpcFaction? expected) =>
        Assert.Equal(expected, AbyssalNpcKnowledge.Faction(names));

    [Theory]
    [InlineData("Karybdis Tyrannos", NpcEwarKind.Scram, false)]
    [InlineData("Scylla Tyrannos", NpcEwarKind.Scram, true)]
    public void TyrannosAgentByName_KarybdisAndScylla_ReturnRecordWithFactionAndKnownEwar(
        string name, NpcEwarKind ewarKind, bool expectedHasEwar)
    {
        var agent = AbyssalNpcKnowledge.TyrannosAgentByName(name);
        Assert.Equal((AbyssalNpcFaction?)AbyssalNpcFaction.VigilantTyrannos, agent?.Faction);
        Assert.Equal((bool?)expectedHasEwar, agent?.KnownEwar.Contains(ewarKind));
    }
}
