using System.Linq;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Fit exporter: an EsiFitting round-trips through EFT and DNA. Uses the in-memory
/// <see cref="FakeSdeAccessor"/> so import→export→import is verified deterministically.
/// </summary>
public class FitExporterTests
{
    private static (FitTextImporter Import, FitExporter Export) Build()
    {
        var sde = FakeSdeAccessor.WithSampleFit();
        return (new FitTextImporter(sde), new FitExporter(sde));
    }

    private const string Eft =
        "[Rifter, Test Rifter]\n" +
        "Damage Control II\n\n" +
        "1MN Afterburner II\n\n" +
        "200mm AutoCannon II, EMP S\n" +
        "200mm AutoCannon II, EMP S\n\n" +
        "Hobgoblin II x5\n" +
        "Nanite Repair Paste x10";

    [Fact]
    public void Eft_RoundTrips()
    {
        var (import, export) = Build();
        var original = import.Import(Eft);
        Assert.True(original.Success, original.Error);

        var eftText = export.ToEft(original.Fit!);
        Assert.StartsWith("[Rifter, Test Rifter]", eftText);
        Assert.Contains("200mm AutoCannon II, EMP S", eftText);
        Assert.Contains("Hobgoblin II x5", eftText);
        Assert.Contains("Nanite Repair Paste x10", eftText);

        // Re-import the exported text: same ship + same item multiset.
        var reparsed = import.Import(eftText);
        Assert.True(reparsed.Success, reparsed.Error);
        Assert.Equal(original.Fit!.ShipTypeId, reparsed.Fit!.ShipTypeId);
        Assert.Equal(
            original.Fit.Items.OrderBy(i => i.TypeId).ThenBy(i => i.Flag).Select(i => (i.TypeId, i.Quantity)),
            reparsed.Fit.Items.OrderBy(i => i.TypeId).ThenBy(i => i.Flag).Select(i => (i.TypeId, i.Quantity)));
    }

    [Fact]
    public void Dna_RoundTrips()
    {
        var (import, export) = Build();
        var original = import.Import(Eft);
        Assert.True(original.Success, original.Error);

        var dna = export.ToDna(original.Fit!);
        Assert.StartsWith("587:", dna);
        Assert.EndsWith("::", dna);

        var reparsed = import.Import(dna);
        Assert.True(reparsed.Success, reparsed.Error);
        Assert.Equal(587, reparsed.Fit!.ShipTypeId);

        // DNA aggregates by typeId, so compare total quantity per typeId (slot layout is intentionally dropped).
        var originalTotals = original.Fit!.Items.GroupBy(i => i.TypeId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var reparsedTotals = reparsed.Fit.Items.GroupBy(i => i.TypeId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        Assert.Equal(originalTotals, reparsedTotals);
    }

    [Fact]
    public void Dna_AggregatesRepeatedModules()
    {
        var (import, export) = Build();
        var fit = import.Import("[Rifter, x]\n200mm AutoCannon II x3").Fit!;
        var dna = export.ToDna(fit);
        Assert.Contains("2889;3", dna); // three turrets collapse to one typeId;qty entry
    }
}
