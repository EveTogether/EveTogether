using EveUtils.Client.Clipboard;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
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

    [Fact]
    public void ParseInventory_CloseTextColumnCardinality_RejectsAmbiguousNames()
    {
        const string text = "Group one\tBaryon Exotic Plasma S Blueprint\t1\r\nGroup two\tBaryon Exotic Plasma S Blueprint\t2\r\nGroup three\tOther Blueprint\t3\r\nGroup four\tFinal Blueprint\t4";

        var rows = ClipboardInventoryParser.Parse(text);

        Assert.Empty(rows);
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
