using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-277: application from the hit-quality word of every OUTGOING line, per weapon, against the target that weapon is
/// shooting now. The parser always kept the word, but it was counted for both directions together and the weapon was
/// thrown away, so the hit rate described nobody's shooting.
/// </summary>
public sealed class ApplicationCountingTests
{
    private static readonly DateTime Start = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Lasers = "Mega Pulse Laser II";
    private const string Drones = "Acolyte II";
    private const string Hams = "Nova Rage Heavy Assault Missile";

    private static WeaponClass Classify(string weapon) => weapon switch
    {
        Lasers => WeaponClass.Turret,
        Drones => WeaponClass.Drone,
        _ => WeaponClassifier.Classify(null, weapon),
    };

    private static WeaponApplicationTracker Shots(string weapon, string target, params HitQuality[] qualities)
    {
        var tracker = new WeaponApplicationTracker(Classify);
        for (var i = 0; i < qualities.Length; i++)
            tracker.Add(Start.AddSeconds(2 * i), weapon, target, qualities[i], qualities[i] is HitQuality.Misses ? 0 : 1000);
        return tracker;
    }

    private static DateTime After(int shots) => Start.AddSeconds(2 * shots);

    [Fact]
    public void AMixOfWords_ReadsAsTheAverageOfTheirBands()
    {
        // graze .5625 + glance .6875 + miss 0 + hit .875 + graze .5625 + glance .6875 = 3.375 over 6 shots = 56 %.
        var tracker = Shots(Lasers, "Shadow", HitQuality.Grazes, HitQuality.Glances, HitQuality.Misses,
            HitQuality.Hits, HitQuality.Grazes, HitQuality.Glances);

        var lasers = Assert.Single(tracker.Read(After(6)));
        Assert.Equal(56.25, lasers.Percent);
        Assert.Equal(ApplicationVerdict.Ok, lasers.Verdict);
    }

    [Fact]
    public void SolidHits_AreASweetSpot_AndNeverReadAboveAHundred()
    {
        var tracker = Shots(Lasers, "Shadow", HitQuality.Penetrates, HitQuality.Smashes, HitQuality.Penetrates,
            HitQuality.Hits, HitQuality.Smashes, HitQuality.Penetrates);

        var lasers = Assert.Single(tracker.Read(After(6)));
        Assert.Equal(100, lasers.Percent);
        Assert.Equal(ApplicationVerdict.SweetSpot, lasers.Verdict);
    }

    [Fact]
    public void OneLuckyWreck_AmongMisses_DoesNotReadAsDecentApplication()
    {
        // Counted ×3, as its damage is, one wreck would lift nine misses to 30 %. It counts as the top of the smash
        // band: grouped turrets wreck almost never, so a crit is no sign of a sweet spot.
        var tracker = Shots(Lasers, "Shadow's Wingman", HitQuality.Wrecks, HitQuality.Misses, HitQuality.Misses,
            HitQuality.Misses, HitQuality.Misses, HitQuality.Misses, HitQuality.Misses, HitQuality.Misses,
            HitQuality.Misses, HitQuality.Misses);

        var lasers = Assert.Single(tracker.Read(After(10)));
        Assert.Equal(14.9, lasers.Percent ?? -1, 3);
        Assert.Equal(ApplicationVerdict.Adjust, lasers.Verdict);
    }

    [Fact]
    public void Missiles_GetNoPercentage_HoweverManyHitsTheyLog()
    {
        // Every missile line says "Hits", however the missile lands; a percentage off that would be made up.
        var tracker = Shots(Hams, "Offertory Sigil", Enumerable.Repeat(HitQuality.Hits, 10).ToArray());

        var missiles = Assert.Single(tracker.Read(After(10)));
        Assert.Equal(WeaponClass.Missile, missiles.Class);
        Assert.Null(missiles.Percent);
        Assert.Equal(ApplicationVerdict.NotMeasurable, missiles.Verdict);
        Assert.Equal("○ APPLICATION n/a", new EveUtils.Client.ViewModels.DpsViewModel { Application = tracker.Summarize(After(10)) }.ApplicationText);
    }

    [Fact]
    public void AnUnresolvedWeapon_ThatOnlyEverWritesHits_IsNotGivenAPercentageEither()
    {
        var tracker = Shots("Some Future Launcher", "Rat", Enumerable.Repeat(HitQuality.Hits, 8).ToArray());

        Assert.Equal(ApplicationVerdict.NotMeasurable, Assert.Single(tracker.Read(After(8))).Verdict);
    }

    [Fact]
    public void ASwitchOfTarget_StartsTheCountAgain()
    {
        var tracker = new WeaponApplicationTracker(Classify);
        for (var i = 0; i < 6; i++)
            tracker.Add(Start.AddSeconds(2 * i), Lasers, "Shadow's Wingman", HitQuality.Misses, 0);
        tracker.Add(Start.AddSeconds(12), Lasers, "Shadow", HitQuality.Smashes, 2400);
        tracker.Add(Start.AddSeconds(14), Lasers, "Shadow", HitQuality.Penetrates, 1900);

        var lasers = Assert.Single(tracker.Read(Start.AddSeconds(15)));
        Assert.Equal("Shadow", lasers.Target);
        Assert.Equal(2, lasers.Shots);
        Assert.Equal(ApplicationVerdict.NotEnoughShots, lasers.Verdict);
    }

    [Fact]
    public void ShotsOlderThanTheWindow_NoLongerCount()
    {
        var tracker = Shots(Lasers, "Shadow", Enumerable.Repeat(HitQuality.Misses, 8).ToArray());

        Assert.Equal(ApplicationVerdict.Adjust, Assert.Single(tracker.Read(After(8))).Verdict);
        Assert.Empty(tracker.Read(After(8) + WeaponApplicationTracker.Window + TimeSpan.FromSeconds(16)));
    }

    [Fact]
    public void TheTurretsGiveTheVerdict_EvenWhileTheyOnlyMiss_AndTheDronesHit()
    {
        // 12 Sep 2026, 18:00, as the game wrote it: both laser groups missing a frigate while the drones hit it.
        string[] log =
        [
            "[ 2026.09.12 18:00:01 ] (combat) Your group of Mega Pulse Laser II misses Shadow's Wingman completely - Mega Pulse Laser II",
            "[ 2026.09.12 18:00:02 ] (combat) Your group of Mega Pulse Laser II misses Shadow's Wingman completely - Mega Pulse Laser II",
            "[ 2026.09.12 18:00:04 ] (combat) Your group of Mega Pulse Laser II misses Shadow's Wingman completely - Mega Pulse Laser II",
            "[ 2026.09.12 18:00:04 ] (combat) Your group of Mega Pulse Laser II misses Shadow's Wingman completely - Mega Pulse Laser II",
            "[ 2026.09.12 18:00:06 ] (combat) Your group of Mega Pulse Laser II misses Shadow's Wingman completely - Mega Pulse Laser II",
            "[ 2026.09.12 18:00:06 ] (combat) Your group of Mega Pulse Laser II misses Shadow's Wingman completely - Mega Pulse Laser II",
            "[ 2026.09.12 18:00:07 ] (combat) <color=0xff00ffff><b>32</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Shadow's Wingman</b><font size=10><color=0x77ffffff> - Acolyte II - Glances Off",
            "[ 2026.09.12 18:00:07 ] (combat) Your Acolyte II misses Shadow's Wingman completely - Acolyte II",
            "[ 2026.09.12 18:00:08 ] (combat) Your group of Mega Pulse Laser II misses Shadow's Wingman completely - Mega Pulse Laser II",
            "[ 2026.09.12 18:00:11 ] (combat) <color=0xff00ffff><b>37</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Shadow's Wingman</b><font size=10><color=0x77ffffff> - Acolyte II - Glances Off",
            "[ 2026.09.12 18:00:11 ] (combat) <color=0xff00ffff><b>37</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Shadow's Wingman</b><font size=10><color=0x77ffffff> - Acolyte II - Glances Off",
            "[ 2026.09.12 18:00:12 ] (combat) <color=0xff00ffff><b>61</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Shadow's Wingman</b><font size=10><color=0x77ffffff> - Acolyte II - Penetrates",
            "[ 2026.09.12 18:00:12 ] (combat) <color=0xff00ffff><b>55</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Shadow's Wingman</b><font size=10><color=0x77ffffff> - Acolyte II - Hits",
            "[ 2026.09.12 18:00:13 ] (combat) <color=0xff00ffff><b>70</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Shadow's Wingman</b><font size=10><color=0x77ffffff> - Acolyte II - Smashes",
        ];
        var tracker = new WeaponApplicationTracker(Classify);
        foreach (var combat in log.Select(LogLineParser.Parse).OfType<CombatEvent>())
            tracker.Add(DateTime.SpecifyKind(combat.Timestamp, DateTimeKind.Utc), combat.Weapon ?? string.Empty, combat.Target,
                combat.Quality, combat.Amount);

        var readings = tracker.Read(new DateTime(2026, 9, 12, 18, 0, 14, DateTimeKind.Utc));
        var summary = tracker.Summarize(new DateTime(2026, 9, 12, 18, 0, 14, DateTimeKind.Utc));

        Assert.Equal(Lasers, readings[0].Weapon);
        Assert.Equal(0, readings[0].Percent);
        Assert.Equal(ApplicationVerdict.Adjust, summary.Verdict);
        Assert.Equal(ApplicationVerdict.Ok, readings.Single(reading => reading.Weapon == Drones).Verdict);
        // Drones: three glances, a miss, a penetrate, a hit and a smash = 5.43 over 7 shots.
        Assert.Equal("Mega Pulse Laser II 0% · Acolyte II 78%", summary.Breakdown);
    }

    [AvaloniaFact]
    public async Task OnlyOutgoingShots_CountTowardsApplication_AndTheHitRate()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        const int characterId = 90000123;
        gamelog.MapCharacter(characterId, "Pilot");

        // The rats' shots at you carry the same words — "97 from Shadow's Goon - Glances Off" — and must not be read
        // as your application, nor as your hit rate.
        var at = DateTime.UtcNow;
        for (var i = 0; i < 8; i++)
            await gamelog.AddHitAsync("Pilot", DamageDirection.Incoming, 97, "Shadow's Goon", HitQuality.Glances, at,
                "Scourge Heavy Missile");
        Assert.Equal(ApplicationVerdict.Idle, gamelog.SampleApplication("Pilot").Verdict);

        for (var i = 0; i < 6; i++)
            await gamelog.AddHitAsync("Pilot", DamageDirection.Outgoing, 0, "Shadow's Wingman", HitQuality.Misses, at, Lasers);

        Assert.Equal(ApplicationVerdict.Adjust, gamelog.SampleApplication("Pilot").Verdict);
        var snapshot = gamelog.Snapshot("Pilot");
        Assert.Equal(6, snapshot.Shots);
        Assert.Equal(0, snapshot.Hits);
        Assert.Empty(snapshot.Qualities);
        Assert.Equal(8 * 97, snapshot.TotalReceived);
    }

    [AvaloniaFact]
    public async Task AMembersApplication_TravelsOnTheFleetSample()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        const int characterId = 90000123;
        gamelog.MapCharacter(characterId, "Pilot");

        var at = DateTime.UtcNow;
        foreach (var quality in new[] { HitQuality.Penetrates, HitQuality.Smashes, HitQuality.Hits, HitQuality.Penetrates, HitQuality.Smashes, HitQuality.Penetrates })
            await gamelog.AddHitAsync("Pilot", DamageDirection.Outgoing, 1200, "Shadow", quality, at, Lasers);

        var sample = gamelog.Sample(7, characterId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .Single(s => s.Kind == MetricKind.Application);
        var received = ApplicationSummary.FromWire(sample.Value, sample.Text);

        Assert.True(MetricShareSnapshot.IsCombat(MetricKind.Application)); // behind the combat share switch
        Assert.Equal(ApplicationVerdict.SweetSpot, received.Verdict);
        Assert.Equal($"{Lasers} 100%", received.Breakdown);
    }

    [Theory]
    [InlineData(ApplicationVerdict.Idle)]
    [InlineData(ApplicationVerdict.NotEnoughShots)]
    [InlineData(ApplicationVerdict.NotMeasurable)]
    public void AVerdictWithoutAPercentage_CrossesTheWire_AsItself(ApplicationVerdict verdict)
    {
        var sent = new ApplicationSummary(verdict, null, "Nova Rage Heavy Assault Missile n/a");

        var received = ApplicationSummary.FromWire(sent.ToWireValue(), sent.Breakdown);

        Assert.True(sent.ToWireValue() < 0);
        Assert.Equal(verdict, received.Verdict);
        Assert.Null(received.Percent);
    }

    [Theory]
    [InlineData(42.4, ApplicationVerdict.Adjust)]
    [InlineData(55, ApplicationVerdict.Ok)]
    [InlineData(80, ApplicationVerdict.SweetSpot)]
    public void AMeasuredPercentage_CrossesTheWire_AndReadsAsTheSameVerdict(double percent, ApplicationVerdict verdict)
    {
        var received = ApplicationSummary.FromWire(new ApplicationSummary(verdict, percent, null).ToWireValue(), null);

        Assert.Equal(percent, received.Percent);
        Assert.Equal(verdict, received.Verdict);
    }

    [Fact]
    public void ACodeANewerClientMaySend_ReadsAsNoReading()
    {
        Assert.Equal(ApplicationVerdict.Idle, ApplicationSummary.FromWire(-42, null).Verdict);
    }
}
