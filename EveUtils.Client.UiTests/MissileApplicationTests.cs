using System;
using System.Linq;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-282: missile application. A missile always logs "Hits", so each volley is read against a full volley at that
/// target — from the fit when it is known, learned from the volleys when not — after the target's strongest layer.
/// The volleys below are Jithran's own, from his Nova Rage HAM Caracal: 1,192 a volley on the Offertory Sigil's armour
/// (a hauler of 270 m signature), 56 to 186 on a Centii Plague (a 35 m frigate).
/// </summary>
public sealed class MissileApplicationTests
{
    private const string Hams = "Nova Rage Heavy Assault Missile";
    private const string Sigil = "Offertory Sigil";
    private const string Plague = "Centii Plague";

    // His Caracal's volley as the dogma engine works it out from the fit, his skills and no implants: 5 launchers,
    // three Ballistic Control System IIs, Warhead Upgrades IV, Heavy Assault Missiles V, Guided Missile Precision V and
    // Target Navigation Prediction IV. The log agrees: 1,325 on the Sigil's hull, 1,192 (×0.9) on its armour.
    private static readonly MissileVolley Caracal = new(1324.83, 161.25, 121.8);

    // The SDE's own figures for the three types: damage and explosion of the missile, and hit points, resonances,
    // signature and top speed of the two NPCs (hull resonances left out, as the SDE does: 1.0).
    private static readonly FakeSdeAccessor Sde = new FakeSdeAccessor()
        .Add(24488, Hams, 657, 8).Attr(24488, 116, 155.3).Attr(24488, 654, 215).Attr(24488, 653, 87).Attr(24488, 1353, 0.92)
        .Add(77066, Sigil, 4747, 11)
        .Attr(77066, 263, 1000).Attr(77066, 265, 11000).Attr(77066, 9, 1500)
        .Attr(77066, 271, 0.45).Attr(77066, 274, 0.54).Attr(77066, 273, 0.72).Attr(77066, 272, 0.9)
        .Attr(77066, 267, 0.45).Attr(77066, 270, 0.54).Attr(77066, 269, 0.72).Attr(77066, 268, 0.9)
        .Attr(77066, 552, 270).Attr(77066, 37, 100)
        .Add(17072, Plague, 1000, 11)
        .Attr(17072, 263, 200).Attr(17072, 265, 250).Attr(17072, 9, 200)
        .Attr(17072, 271, 0.93).Attr(17072, 274, 0.83).Attr(17072, 273, 0.73).Attr(17072, 272, 0.63)
        .Attr(17072, 267, 0.93).Attr(17072, 270, 0.83).Attr(17072, 269, 0.73).Attr(17072, 268, 0.63)
        .Attr(17072, 552, 35).Attr(17072, 37, 440);

    // 13 Sep 2026 12:26:21–39, one Sigil to its death: five full volleys on armour, one breaking through to the hull,
    // and the last one logged as only the hit points it had left.
    private static readonly (string At, int Amount)[] SigilKill =
        [("12:26:21", 1192), ("12:26:24", 1192), ("12:26:27", 1192), ("12:26:30", 1192), ("12:26:34", 1192), ("12:26:36", 1282), ("12:26:39", 160)];

    // 2 Sep 2026 12:52:08–36, one Centii Plague.
    private static readonly (string At, int Amount)[] PlagueFight =
        [("12:52:08", 156), ("12:52:11", 131), ("12:52:14", 122), ("12:52:17", 186), ("12:52:20", 56), ("12:52:26", 119), ("12:52:29", 119), ("12:52:33", 119), ("12:52:36", 134)];

    private static WeaponApplicationTracker Tracker(MissileVolley? fit) =>
        new(weapon => WeaponClassifier.Classify(Sde, weapon), new MissileGauge(() => Sde, _ => fit));

    // Through the real parser, in the shape the game writes an outgoing missile line.
    private static DateTime Fire(WeaponApplicationTracker tracker, string date, string target, params (string At, int Amount)[] volleys)
    {
        var last = DateTime.MinValue;
        foreach (var (at, amount) in volleys)
        {
            var line = $"[ {date} {at} ] (combat) <color=0xff00ffff><b>{amount}</b> <color=0x77ffffff><font size=10>to</font> " +
                       $"<b><color=0xffffffff>{target}</b><font size=10><color=0x77ffffff> - {Hams} - Hits";
            var hit = Assert.IsType<CombatEvent>(LogLineParser.Parse(line));
            last = DateTime.SpecifyKind(hit.Timestamp, DateTimeKind.Utc);
            tracker.Add(last, hit.Weapon ?? string.Empty, hit.Target, hit.Quality, hit.Amount);
        }
        return last.AddSeconds(1);
    }

    [Fact]
    public void HamsOnTheSigil_AreASweetSpot_AndOnAFrigate_Adjust_SayingItIsTooSmallAndTooFast()
    {
        var tracker = Tracker(Caracal);

        var sigil = tracker.Summarize(Fire(tracker, "2026.09.13", Sigil, SigilKill));
        Assert.Equal(ApplicationVerdict.SweetSpot, sigil.Verdict);
        Assert.Equal(100, sigil.Percent ?? 0, 0);

        var plague = Tracker(Caracal);
        var now = Fire(plague, "2026.09.02", Plague, PlagueFight);
        var summary = plague.Summarize(now);
        var reading = Assert.Single(plague.Read(now));

        // Median of the nine volleys against 1,324.8 × 0.63: 122 of 834.6.
        Assert.Equal(ApplicationVerdict.Adjust, summary.Verdict);
        Assert.Equal(14.6, summary.Percent ?? -1, 1);
        Assert.Equal("target too small and too fast: at most 22% even standing still; web and paint it, or use smaller missiles",
            reading.Cause);
        Assert.Equal($"{Hams} 15% ({reading.Cause})", summary.Breakdown);
    }

    [Fact]
    public void TheLastVolleyOnADyingTarget_DoesNotPullTheFigureDown()
    {
        // Averaged, the 160 would make this an 88 % Sigil; the median is not moved by one volley.
        var tracker = Tracker(Caracal);

        var reading = Assert.Single(tracker.Read(Fire(tracker, "2026.09.13", Sigil, SigilKill)));

        Assert.True(reading.Percent > 99.9, $"read {reading.Percent}");
        Assert.Null(reading.Cause);
    }

    [Fact]
    public void WithoutAFit_AFrigateOnlyFight_IsLearning_NotAMadeUpPercentage()
    {
        var tracker = Tracker(fit: null);

        var learning = tracker.Summarize(Fire(tracker, "2026.09.02", Plague, PlagueFight));

        // A HAM never lands fully on a 35 m frigate, so its best hit there says nothing about a full volley.
        Assert.Equal(ApplicationVerdict.Learning, learning.Verdict);
        Assert.Null(learning.Percent);
        Assert.Equal($"{Hams} learning", learning.Breakdown);
        Assert.Equal("○ LEARNING", new DpsViewModel { Application = learning }.ApplicationText);
    }

    [Fact]
    public void WithoutAFit_OneTargetItFullyHits_TeachesTheVolley_ForEveryOtherTarget()
    {
        var tracker = Tracker(fit: null);
        Fire(tracker, "2026.09.13", Sigil, SigilKill);

        // Learned from the Sigil: 1,282 through its weakest layer, the hull — at or under the real 1,324.8.
        var plague = tracker.Summarize(Fire(tracker, "2026.09.13", Plague,
            PlagueFight.Select((volley, i) => ($"12:27:{10 + 3 * i:00}", volley.Amount)).ToArray()));

        Assert.Equal(ApplicationVerdict.Adjust, plague.Verdict);
        Assert.Equal(122 / (1282 * 0.63) * 100, plague.Percent ?? -1, 1);
    }

    [Fact]
    public void AVolleyLoggedOverTwoLines_CountsAsOneVolley()
    {
        // The game splits a volley over lines when its missiles land a tick apart (two light-missile lines of 332 and
        // 498 every volley on 29 Aug). Read line by line, every full volley here would read as half of one.
        var tracker = Tracker(Caracal);

        var summary = tracker.Summarize(Fire(tracker, "2026.09.13", Sigil,
            ("12:26:21", 596), ("12:26:21", 596), ("12:26:24", 596), ("12:26:24", 596),
            ("12:26:27", 596), ("12:26:27", 596), ("12:26:30", 596), ("12:26:30", 596)));

        Assert.Equal(ApplicationVerdict.SweetSpot, summary.Verdict);
    }

    [Fact]
    public void AMissileAtAPlayerShip_IsNotMeasurable_ItsResistsAreUnknown()
    {
        var tracker = Tracker(Caracal);

        var reading = Assert.Single(tracker.Read(Fire(tracker, "2026.09.01", "001Ventuainen Inkura[KISI](Rifter)",
            ("11:40:01", 26), ("11:40:04", 33), ("11:40:07", 27), ("11:40:10", 34))));

        Assert.Equal(ApplicationVerdict.NotMeasurable, reading.Verdict);
        Assert.Null(reading.Percent);
        Assert.Equal("not an NPC; its resists are unknown", reading.Cause);
    }

    [Fact]
    public void AMissilesFigureAndItsCause_RideTheFleetSample()
    {
        var tracker = Tracker(Caracal);
        var sent = tracker.Summarize(Fire(tracker, "2026.09.02", Plague, PlagueFight));

        var received = ApplicationSummary.FromWire(sent.ToWireValue(), sent.Breakdown);

        Assert.Equal(ApplicationVerdict.Adjust, received.Verdict);
        Assert.Equal(sent.Percent ?? -1, received.Percent ?? -2, 6);
        Assert.Contains("target too small and too fast", received.Breakdown);
    }

    [Fact]
    public void TheChipTooltip_PutsEachWeaponOnItsOwnLine_AboveHowItIsWorkedOut()
    {
        var meter = new DpsViewModel
        {
            Application = new ApplicationSummary(ApplicationVerdict.Adjust, 15,
                $"{Hams} 15% (target too fast; web or paint it) · Acolyte II 78%"),
        };

        var lines = meter.ApplicationTip.Split(Environment.NewLine);

        Assert.Equal($"{Hams} 15% (target too fast; web or paint it)", lines[0]);
        Assert.Equal("Acolyte II 78%", lines[1]);
        Assert.Equal(DpsViewModel.ApplicationMethod, lines[^1]);
    }
}
