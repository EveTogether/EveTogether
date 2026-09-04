using EveUtils.Client.Clipboard;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class ClipboardCaptureParserTests
{
    [Fact]
    public void ParseInventory_ReorderedColumnsAndBothLocales_MapsAnchoredValues()
    {
        const string text = "Abyssal Filaments\t42.237,65 ISK\tAgitated Exotic Filament\t0,10 m3\t1\t\r\nAbyssal Filaments\t6,980.63 ISK\tAgitated Firestorm Filament\t0.10 m3\t2\t\r\nAbyssal Filaments\t32.852,29 ISK\tAgitated Gamma Filament\t0,20 m3\t3\t";

        var rows = ClipboardInventoryParser.Parse(text);

        Assert.Collection(rows,
            row =>
            {
                Assert.Equal("Agitated Exotic Filament", row.Name);
                Assert.Equal(1, row.Quantity);
                Assert.Equal(0.10m, row.Volume);
                Assert.Equal(42237.65m, row.Price);
            },
            row =>
            {
                Assert.Equal("Agitated Firestorm Filament", row.Name);
                Assert.Equal(2, row.Quantity);
                Assert.Equal(0.10m, row.Volume);
                Assert.Equal(6980.63m, row.Price);
            },
            row => Assert.Equal("Agitated Gamma Filament", row.Name));
    }

    [Fact]
    public void ParseInventory_GroupedAndUnreadableQuantities_KeepReadableRows()
    {
        const string text = "First item\t1\r\nSecond item\t5.000\r\nThird item\tunknown";

        var rows = ClipboardInventoryParser.Parse(text);

        Assert.Collection(rows,
            row => Assert.Equal(1, row.Quantity),
            row => Assert.Equal(5000, row.Quantity),
            row => Assert.Null(row.Quantity));
    }

    /// <summary>
    /// Close cardinality no longer decides anything on its own: the group column here has MORE distinct values
    /// than the names, so the shape alone picks the wrong one. That is why both columns are offered — whether the
    /// right one wins is settled against the SDE, in <c>ClipboardLootCaptureTests</c>.
    /// </summary>
    [Fact]
    public void ParseInventory_CloseTextColumnCardinality_OffersBothColumnsRatherThanDeciding()
    {
        const string text = "Group one\tBaryon Exotic Plasma S Blueprint\t1\r\nGroup two\tBaryon Exotic Plasma S Blueprint\t2\r\nGroup three\tOther Blueprint\t3\r\nGroup four\tFinal Blueprint\t4";

        Assert.Equal(
            [["Group one", "Group two", "Group three", "Group four"],
                ["Baryon Exotic Plasma S Blueprint", "Baryon Exotic Plasma S Blueprint", "Other Blueprint", "Final Blueprint"]],
            ClipboardInventoryParser.ParseNameColumnCandidates(text)
                .Select(column => column.Select(item => item.Name).ToArray()));
    }

    [Fact]
    public void ParseInventory_SingleAmbiguousRow_OffersCandidatesWithoutGuessing()
    {
        const string text = "Ultraviolet M\t1\tFrequency Crystal\tMedium\t\t1 m3\t2.350,77 ISK";

        Assert.Empty(ClipboardInventoryParser.Parse(text));
        Assert.Equal(
            [["Ultraviolet M"], ["Frequency Crystal"], ["Medium"]],
            ClipboardInventoryParser.ParseNameColumnCandidates(text)
                .Select(column => column.Select(item => item.Name).ToArray()));
    }

    [Fact]
    public void ParseInventory_IconsRowsAndAmbiguousNumber_LeavesOptionalValuesEmpty()
    {
        const string text = "Entropic Radiation Sink I Blueprint\t\t\r\nTriglavian Survey Database\t682\t\r\nCrystalline Isogen-10\t209\t\r\nUncertain Price\t\t1.234 ISK";

        var rows = ClipboardInventoryParser.Parse(text);

        Assert.Collection(rows,
            row =>
            {
                Assert.Equal("Entropic Radiation Sink I Blueprint", row.Name);
                Assert.Null(row.Quantity);
                Assert.Null(row.Volume);
                Assert.Null(row.Price);
            },
            row => Assert.Equal(682, row.Quantity),
            row => Assert.Equal(209, row.Quantity),
            row => Assert.Null(row.Price));
    }

    [Fact]
    public void ParseFit_FitCapture_UsesExistingFitTextImporter()
    {
        const string text = "[Jackdaw, Jackdaw - T1/T2 - D]\r\nBallistic Control System II";
        var expected = FitImportResult.Failed("SDE is not loaded.");
        var importer = new RecordingFitTextImporter(expected);
        var parser = new ClipboardCaptureParser(importer);

        var parsed = parser.ParseFit(new ClipboardCapture(ClipboardShape.Fit, text));

        Assert.Same(expected, parsed);
        Assert.Equal(text, importer.ImportedText);
    }

    /// <summary>EVE's Icons view, copied verbatim: two columns, and a blueprint carries no quantity at all.</summary>
    [Fact]
    public void ParseInventory_IconsView_KeepsEveryRow_IncludingTheOnesWithoutAQuantity()
    {
        string text = _Fixture("inventory-icons.txt");

        Assert.Equal(ClipboardShape.Inventory, ClipboardShapeRecogniser.Recognise(text));
        var rows = ClipboardInventoryParser.Parse(text);

        Assert.Equal(40, rows.Count);
        Assert.Equal("Entropic Radiation Sink I Blueprint", rows[0].Name);
        Assert.Null(rows[0].Quantity);
        Assert.Equal(209, rows.Single(row => row.Name == "Crystalline Isogen-10").Quantity);
        Assert.Equal(9, rows.Count(row => row.Quantity is null));
    }

    /// <summary>The Details view, which already worked — here so this fixture cannot regress unnoticed.</summary>
    [Fact]
    public void ParseInventory_DetailsView_ReadsNameQuantityVolumeAndPrice()
    {
        string text = _Fixture("inventory-detail.txt");

        Assert.Equal(ClipboardShape.Inventory, ClipboardShapeRecogniser.Recognise(text));
        var rows = ClipboardInventoryParser.Parse(text);

        Assert.Equal(40, rows.Count);
        var isogen = rows.Single(row => row.Name == "Crystalline Isogen-10");
        Assert.Equal(209, isogen.Quantity);
        Assert.NotNull(isogen.Volume);
        Assert.NotNull(isogen.Price);
    }

    // ET-175 AC-1: the one real mission capture this project has (ET-129, via ET-172's grooming), byte for byte.
    [Fact]
    public void Recognise_RaymondsMissionCapture_YieldsMissionShape()
    {
        string text = _Fixture("mission-aralin-jick.txt");

        Assert.Equal(ClipboardShape.Mission, ClipboardShapeRecogniser.Recognise(text));
    }

    // ET-175 AC-2: a table row whose name column merely ends in the word "Objectives" still has more fields on
    // the same line, so it fails the mission header's whole-line match and stays an ordinary inventory row.
    [Fact]
    public void Recognise_InventoryRowEndingInObjectivesText_StillWinsAsInventoryNotMission()
    {
        const string text = "Republic Fleet Objectives\t5\r\nRepublic Fleet Small Armor Repairer\t2";

        Assert.Equal(ClipboardShape.Inventory, ClipboardShapeRecogniser.Recognise(text));
    }

    [Fact]
    public void ParseMission_RaymondsCapture_ReadsNameAgentRewardsAndBonusWindow()
    {
        string text = _Fixture("mission-aralin-jick.txt");

        var capture = ClipboardMissionParser.Parse(text);

        Assert.NotNull(capture);
        Assert.Equal("Aralin Jick", capture!.ObjectivesHeaderName); // AC-3
        Assert.Equal("Aralin Jick", capture.AgentName); // AC-4
        Assert.Equal(21600, capture.BonusWindowSeconds); // AC-6
        Assert.Collection(capture.Rewards,
            reward =>
            {
                Assert.Equal(RunParameterKey.Isk, reward.ParameterKey);
                Assert.Equal(1000000m, reward.Amount); // AC-5: "1.000.000" is thousands, not a decimal
            },
            reward =>
            {
                Assert.Equal(RunParameterKey.BonusIsk, reward.ParameterKey);
                Assert.Equal(1610000m, reward.Amount);
            });

        // AC-5: the same capture also carries "0,6" as a comma decimal, in the location row the parser above
        // never reads. The shared TryParseLocalNumber must still read it correctly, not just the reward form.
        Assert.True(ClipboardInventoryParser.TryParseLocalNumber("0,6", out var locationNumber));
        Assert.Equal(0.6m, locationNumber);
    }

    [Fact]
    public void ParseMission_EdgeCases_RefuseRatherThanGuess()
    {
        // AC-3: a first line without the "<agent> Objectives" header yields no mission name, while the rest of
        // the block still reads normally.
        const string missingHeader =
            "Objectives\nThe following objectives must be completed to finish the mission:\n\nReport to Aralin Jick";
        var missingHeaderCapture = ClipboardMissionParser.Parse(missingHeader);
        Assert.NotNull(missingHeaderCapture);
        Assert.Null(missingHeaderCapture!.ObjectivesHeaderName);
        Assert.Equal("Aralin Jick", missingHeaderCapture.AgentName);

        // AC-4: the location line is made to contradict "Report to Aralin Jick" on purpose. The parser never
        // reads it, so the agent name follows "Report to" regardless.
        const string contradictingLocation =
            "Aralin Jick Objectives\nThe following objectives must be completed to finish the mission:\n\n" +
            "Report to Aralin Jick\n \tAgent Location\t0,6 Jita IV - Moon 4 - Caldari Navy Assembly Plant";
        Assert.Equal("Aralin Jick", ClipboardMissionParser.Parse(contradictingLocation)!.AgentName);

        // AC-7: an item reward beside an ISK one. EVE Journal's own regexes drop a line shaped exactly like this
        // one without a trace [gemeten, ET-172]; it must still count as a reward here.
        const string itemReward =
            "Aralin Jick Objectives\nThe following objectives must be completed to finish the mission:\n\n" +
            "Report to Aralin Jick\n\nRewards\nThe following rewards will be yours if you complete this mission:\n" +
            " \t1.000.000 ISK\n \t1 × Republic Fleet Small Armor Repairer";
        var rewards = ClipboardMissionParser.Parse(itemReward)!.Rewards;
        Assert.Equal(2, rewards.Count);
        Assert.Equal(RunParameterKey.Item, rewards[1].ParameterKey);
        Assert.Equal("Republic Fleet Small Armor Repairer", rewards[1].ItemName);
        Assert.Equal(1, rewards[1].ItemQuantity);

        // AC-8: a fragment copied without its block header carries no recognisable structure at all.
        const string halfBlock =
            "The following rewards will be awarded to you as a bonus if you complete the mission within 6 hours:\n \t1.610.000 ISK";
        Assert.Null(ClipboardMissionParser.Parse(halfBlock));
    }

    private static string _Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;

        return File.ReadAllText(Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            "EveUtils.Client.UiTests", "Fixtures", name));
    }

    private sealed class RecordingFitTextImporter(FitImportResult importResult) : IFitTextImporter
    {
        public string? ImportedText { get; private set; }

        public FitTextFormat Detect(string text) => default;

        public FitImportResult Import(string text)
        {
            ImportedText = text;
            return importResult;
        }
    }
}
