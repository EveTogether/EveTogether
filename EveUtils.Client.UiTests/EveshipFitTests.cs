using System.Linq;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// eveship.fit interop: export to a v3 URL and import v3 + eft links. The decode side is cross-checked
/// against a real eveship-generated v3 string (proving our gzip+base64+CSV reader matches eveship's encoder), and
/// our own export round-trips back through the importer.
/// </summary>
public class EveshipFitTests
{
    // A real eveship.fit v3 payload, copied live from eveship.fit 2026-06-10.
    // eveship numbers slots 1-based (High,1 = first high slot); decoding it proves we map those onto ESI's 0-based
    // HiSlot0… flags. (An earlier 0-based fixture turned out to be our own exporter's output, not eveship's.)
    private const string RealV3 =
        "v3:H4sIAAAAAAAAA33PMQrCQBSE4T6n8ABTvHlv3TWlhZBCG/ECkoRkUYlooh5fEEHUbNr5mvmvbTyDtJBjx9nq0fWxnBX7+wHZqauGY411dwehqjmxLPt4q79Ix2lTV3E4gfAilkCFE0mYIfemCXQgF/MEzuFEwy8WsWlBULz9fX2ZTphNmBu3bWxAGC34MVIYg376yv2l6aDOBcJE5D24EDxBzZ6Mvj5gpwEAAA==";

    private static (FitTextImporter Import, FitExporter Export) Build()
    {
        var sde = FakeSdeAccessor.WithSampleFit();
        return (new FitTextImporter(sde), new FitExporter(sde));
    }

    [Fact]
    public void Decode_RealEveshipV3Payload_MapsOneBasedSlotsToZeroBasedFlags()
    {
        var (import, _) = Build();
        var result = import.Import(RealV3);

        Assert.True(result.Success, result.Error);
        Assert.Equal(11379, result.Fit!.ShipTypeId);
        Assert.Equal("T1 Exotic Hawk", result.Fit.Name);

        // Off-by-one regression: eveship's High,1..4 must land on HiSlot0..3 (not HiSlot1..4). Before the fix the
        // first slot was empty and everything sat one slot too high.
        var highFlags = result.Fit.Items.Where(i => i.TypeId == 10631).Select(i => i.Flag).OrderBy(f => f).ToArray();
        Assert.Equal(new[] { "HiSlot0", "HiSlot1", "HiSlot2", "HiSlot3" }, highFlags);
        Assert.DoesNotContain(result.Fit.Items, i => i.Flag == "HiSlot4");

        // The other racks map the same way: first low/mid/rig slot is index 0.
        Assert.Contains(result.Fit.Items, i => i.TypeId == 22291 && i.Flag == "LoSlot0");
        Assert.Contains(result.Fit.Items, i => i.TypeId == 6003 && i.Flag == "MedSlot0");
        Assert.Contains(result.Fit.Items, i => i.TypeId == 31376 && i.Flag == "RigSlot0");
    }

    [Fact]
    public void Detect_RecognisesEveshipUrlAndRawPayloads()
    {
        var (import, _) = Build();
        Assert.Equal(FitTextFormat.Eveship, import.Detect("https://eveship.fit/?fit=v3:abc"));
        Assert.Equal(FitTextFormat.Eveship, import.Detect("v3:abc"));
        Assert.Equal(FitTextFormat.Eveship, import.Detect("eft:abc"));
        Assert.Equal(FitTextFormat.Eft, import.Detect("[Rifter, x]\nDamage Control II"));
    }

    [Fact]
    public void Export_RoundTripsThroughImport()
    {
        var (import, export) = Build();
        var original = import.Import(
            "[Rifter, Test Rifter]\nDamage Control II\n\n1MN Afterburner II\n\n200mm AutoCannon II, EMP S\n\nHobgoblin II x5\nNanite Repair Paste x10");
        Assert.True(original.Success, original.Error);

        var url = export.ToEveshipUrl(original.Fit!);
        Assert.StartsWith("https://eveship.fit/?fit=v3:", url);

        var reparsed = import.Import(url);
        Assert.True(reparsed.Success, reparsed.Error);
        Assert.Equal(587, reparsed.Fit!.ShipTypeId);
        Assert.Equal("Test Rifter", reparsed.Fit.Name);
        // Same item multiset (typeId + quantity); slot flags are reconstructed from the v3 slot data.
        Assert.Equal(
            original.Fit!.Items.GroupBy(i => i.TypeId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity)),
            reparsed.Fit.Items.GroupBy(i => i.TypeId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity)));
        // The loaded charge kept its launcher's high slot.
        Assert.Contains(reparsed.Fit.Items, i => i.TypeId == 12608 && i.Flag.StartsWith("HiSlot"));
        Assert.Contains(reparsed.Fit.Items, i => i.TypeId == 2456 && i.Flag == "DroneBay" && i.Quantity == 5);
    }

    [Fact]
    public void Import_EftEveshipPayload_UsesEftPath()
    {
        var (import, _) = Build();
        var eftPayload = "eft:" + EveshipFitCodec.Compress("[Rifter, From Eveship]\nDamage Control II\n200mm AutoCannon II, EMP S");
        var result = import.Import(eftPayload);

        Assert.True(result.Success, result.Error);
        Assert.Equal(587, result.Fit!.ShipTypeId);
        Assert.Equal("From Eveship", result.Fit.Name);
        Assert.Contains(result.Fit.Items, i => i.TypeId == 2048 && i.Flag == "LoSlot0");
    }

    [Fact]
    public void Import_LegacyV1V2_AreRejectedWithGuidance()
    {
        var (import, _) = Build();
        var v2 = "v2:" + EveshipFitCodec.Compress("11,2048");
        var result = import.Import(v2);
        Assert.False(result.Success);
        Assert.Contains("v1/v2", result.Error!);
    }
}
