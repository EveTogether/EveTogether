using System.IO;
using EveUtils.Client.Composition;
using EveUtils.Shared.Modules.Sde.Storage;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Integration test for the NPC e-war/EHP/signature/speed accessor (ET-367 AC1) against the real SDE store. Skipped
/// automatically when the store is not available (developer machine without an imported SDE, or CI). The ten rows
/// are the ET-342 research's cross-check set (build 3542233); three of them (Entangler/Spearfisher/Obfuscator) also
/// pin the exact range the ticket quotes (10 km web / 9 km scramble / 15 km dampener).
/// </summary>
public sealed class NpcEwarProfileTests
{
    private static readonly string SdePath =
        Path.Combine(ClientDataLocation.DefaultRoot, "sde", "sde.sqlite");

    private static SqliteSdeAccessor? TryOpen()
    {
        if (!File.Exists(SdePath))
            return null;
        var sde = new SqliteSdeAccessor(SdePath);
        return sde.IsAvailable ? sde : null;
    }

    [Theory]
    [InlineData(48088, "Tangling Damavik",             null,   null,   10000.0, null,   null, null, null, 14000.0, null)]
    [InlineData(48247, "Lucid Firewatcher",             null,   5000.0, null,    null,   null, null, null, 40000.0, null)]
    [InlineData(56148, "Devoted Knight",                null,   10000.0, 10000.0, null,  null, null, null, null,    null)]
    [InlineData(56173, "Drainer Pacifier Disparu Troop", null,  9000.0, null,    null,   null, null, null, null,    null)]
    [InlineData(56200, "Thunderchild Disparu Troop",    null,   null,   null,    null,   null, null, null, null,    10000.0)]
    [InlineData(56295, "Lucifer Cynabal",                null,  9000.0, 13000.0, null,   null, null, null, null,    null)]
    [InlineData(47880, "Benthic Abyssal Overmind",       null,  null,   15000.0, null,   null, null, null, null,    null)]
    [InlineData(48235, "Ephialtes Entangler",            null,  null,   10000.0, null,   null, null, null, null,    null)]
    [InlineData(48236, "Ephialtes Spearfisher",          9000.0, null,  null,    null,   null, null, null, null,    null)]
    [InlineData(48239, "Ephialtes Obfuscator",           null,  null,   null,    15000.0, null, null, null, null,   null)]
    public void GetNpcEwarProfile_CrossCheckRows_MatchSde(
        int typeId, string label,
        double? scramble, double? neutralizer, double? webifier, double? sensorDampener,
        double? trackingDisrupt, double? guidanceDisrupt, double? targetPainter, double? remoteArmorRepairer, double? vorton)
    {
        var sde = TryOpen();
        if (sde is null) return; // skip — no SDE

        var profile = sde.GetNpcEwarProfile(typeId);
        Assert.True(profile is not null, $"{label}: expected a non-null profile for typeId {typeId}");
        Assert.Equal(scramble, profile?.ScrambleRange);
        Assert.Equal(neutralizer, profile?.NeutralizerRange);
        Assert.Equal(webifier, profile?.WebifierRange);
        Assert.Equal(sensorDampener, profile?.SensorDampenerRange);
        Assert.Equal(trackingDisrupt, profile?.TrackingDisruptorRange);
        Assert.Equal(guidanceDisrupt, profile?.GuidanceDisruptorRange);
        Assert.Equal(targetPainter, profile?.TargetPainterRange);
        Assert.Equal(remoteArmorRepairer, profile?.RemoteArmorRepairerRange);
        Assert.Equal(vorton, profile?.VortonRange);
    }
}
