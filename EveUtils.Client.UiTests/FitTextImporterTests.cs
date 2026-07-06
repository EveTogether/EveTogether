using System.Linq;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Fit text parsers: EFT + DNA parse + SDE-resolve into the internal EsiFitting model with ESI slot flags
/// derived from the SDE's pre-computed slot table. Uses a small in-memory <see cref="FakeSdeAccessor"/> so the
/// assembler logic (flag allocation, charge pairing, drone/cargo split, warnings) is tested deterministically.
/// </summary>
public class FitTextImporterTests
{
    private static FitTextImporter Importer() => new(FakeSdeAccessor.WithSampleFit());

    [Fact]
    public void Eft_MapsShipModulesChargeDroneCargo_WithSlotFlags()
    {
        var text = string.Join("\n",
            "[Rifter, Test Rifter]",
            "Damage Control II",
            "",
            "1MN Afterburner II",
            "",
            "200mm AutoCannon II, EMP S",
            "",
            "Hobgoblin II x5",
            "Nanite Repair Paste x10");

        var result = Importer().Import(text);

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Warnings);
        var fit = result.Fit!;
        Assert.Equal(587, fit.ShipTypeId);
        Assert.Equal("Test Rifter", fit.Name);
        Assert.Contains(fit.Items, i => i.TypeId == 2048 && i.Flag == "LoSlot0");
        Assert.Contains(fit.Items, i => i.TypeId == 438 && i.Flag == "MedSlot0");
        Assert.Contains(fit.Items, i => i.TypeId == 2889 && i.Flag == "HiSlot0");
        Assert.Contains(fit.Items, i => i.TypeId == 12608 && i.Flag == "HiSlot0"); // charge loaded in the launcher's slot
        Assert.Contains(fit.Items, i => i.TypeId == 2456 && i.Flag == "DroneBay" && i.Quantity == 5);
        Assert.Contains(fit.Items, i => i.TypeId == 28668 && i.Flag == "Cargo" && i.Quantity == 10);
    }

    [Fact]
    public void Eft_RepeatedModule_AllocatesDistinctSlots()
    {
        var result = Importer().Import("[Rifter, x]\n200mm AutoCannon II x2");

        Assert.True(result.Success, result.Error);
        var highSlots = result.Fit!.Items.Where(i => i.TypeId == 2889).Select(i => i.Flag).OrderBy(f => f).ToArray();
        Assert.Equal(["HiSlot0", "HiSlot1"], highSlots);
    }

    [Fact]
    public void Eft_ModuleInTrailingCargoSection_GoesToCargo_NotASlot()
    {
        // A spare Damage Control kept in the cargo hold (a refit) carries an xN in a trailing section and must not be
        // fitted into a low slot — some marauder fits do this and would otherwise be over-tanked.
        var text = string.Join("\n",
            "[Rifter, Refit]",
            "1MN Afterburner II",   // leading slot rack -> fitted
            "",
            "Hobgoblin II x5",      // drones section
            "",
            "Damage Control II x1", // cargo section: a module-typed item kept as cargo
            "Nanite Repair Paste x10");

        var result = Importer().Import(text);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Fit!.Items, i => i.TypeId == 2048 && i.Flag == "Cargo");
        Assert.DoesNotContain(result.Fit.Items, i => i.TypeId == 2048 && i.Flag.StartsWith("LoSlot"));
        Assert.Contains(result.Fit.Items, i => i.TypeId == 438 && i.Flag == "MedSlot0");                  // leading module still fits
        Assert.Contains(result.Fit.Items, i => i.TypeId == 2456 && i.Flag == "DroneBay" && i.Quantity == 5);
    }

    [Fact]
    public void Dna_MapsShipAndItems_WithQuantities()
    {
        var result = Importer().Import("587:2048;1:438;1:2889;2::");

        Assert.True(result.Success, result.Error);
        Assert.Equal(587, result.Fit!.ShipTypeId);
        Assert.Contains(result.Fit.Items, i => i.TypeId == 2048 && i.Flag == "LoSlot0");
        Assert.Contains(result.Fit.Items, i => i.TypeId == 438 && i.Flag == "MedSlot0");
        Assert.Equal(2, result.Fit.Items.Count(i => i.TypeId == 2889)); // qty 2 -> two high slots
    }

    [Fact]
    public void Detect_DistinguishesFormats()
    {
        var importer = Importer();
        Assert.Equal(FitTextFormat.Eft, importer.Detect("[Rifter, x]\nDamage Control II"));
        Assert.Equal(FitTextFormat.Dna, importer.Detect("587:2048;1::"));
        Assert.Equal(FitTextFormat.Unknown, importer.Detect("just some prose, not a fit"));
    }

    [Fact]
    public void UnknownModule_WarnsButStillImports()
    {
        var result = Importer().Import("[Rifter, x]\nDamage Control II\nSome Nonexistent Module");

        Assert.True(result.Success, result.Error);
        Assert.Single(result.Warnings);
        Assert.Contains("Some Nonexistent Module", result.Warnings[0]);
        Assert.Contains(result.Fit!.Items, i => i.TypeId == 2048);
    }

    [Fact]
    public void UnknownShip_Fails()
    {
        var result = Importer().Import("[Nonexistent Ship, x]\nDamage Control II");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void SdeNotLoaded_Fails()
    {
        var result = new FitTextImporter(new FakeSdeAccessor().Offline()).Import("[Rifter, x]\nDamage Control II");

        Assert.False(result.Success);
        Assert.Contains("static data", result.Error!);
    }
}
