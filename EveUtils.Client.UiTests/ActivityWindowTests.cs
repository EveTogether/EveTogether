using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The activity window's frame (ET-98 phase 1): that every section answers for itself with its body shut, that the
/// manual weather/tier survives the window being closed and reopened, that no label ever implies the window valued
/// the loot itself, and that all four faction palettes actually reach it — which on an <see cref="OverlayWindow"/>
/// rather than a <c>ChromedWindow</c> is worked for rather than inherited.
/// </summary>
public class ActivityWindowTests
{
    private static readonly DateTime Anchor = new(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc);

    // ── AC-1 — every section says something, open or shut ───────────────────────────────────────────

    [Theory]
    [InlineData(ActivityKind.Abyssal)]
    [InlineData(ActivityKind.Site)]
    public void EmptyRun_EverySectionSummary_SaysSomething(ActivityKind kind)
    {
        var model = new ActivityWindowViewModel(kind, _Unused());

        Assert.Equal(5, model.Sections.Count);
        foreach (var section in model.Sections)
            Assert.False(string.IsNullOrWhiteSpace(section.HeaderSummary),
                $"{section.Title} is silent with its body shut on an empty {kind} run");
    }

    [Theory]
    [InlineData(ActivityKind.Abyssal)]
    [InlineData(ActivityKind.Site)]
    public void FilledRun_EverySectionSummary_SaysSomething(ActivityKind kind)
    {
        var model = _Filled(kind);
        model.WeatherIndex = 3;
        model.TierIndex = 4;
        model.Refresh(Anchor.AddMinutes(6));

        foreach (var section in model.Sections)
            Assert.False(string.IsNullOrWhiteSpace(section.HeaderSummary),
                $"{section.Title} is silent with its body shut on a filled {kind} run");
    }

    [Fact]
    public void ASectionWaitingOnAnotherTicket_NamesIt_RatherThanShowingSampleData()
    {
        var abyssal = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        Assert.Contains("ET-40", abyssal.Fit.HeaderSummary);
        Assert.Contains("ET-65", abyssal.Loot.HeaderSummary);
        Assert.Contains("ET-80", new ActivityWindowViewModel(ActivityKind.Site, _Unused()).Activity.HeaderSummary);
    }

    [Fact]
    public void InTheAbyss_BountyAndLocationSayWhyTheyAreEmpty()
    {
        var model = _Filled(ActivityKind.Abyssal);

        Assert.Contains("no bounty", model.Bounty.HeaderSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no location", model.Activity.HeaderSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pocket", model.LocationText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("0", model.Bounty.HeaderSummary);
    }

    // ── The clock ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AbyssalClock_CountsDownFromTheAnchor_AndEndsAtTheDeadline()
    {
        var model = _Filled(ActivityKind.Abyssal);
        model.Refresh(Anchor.AddMinutes(6));

        Assert.Equal("TIME LEFT", model.ClockLabel);
        Assert.Equal("14:00", model.ClockText);
        Assert.False(model.IsClockWarning);
        Assert.False(model.IsClockCritical);

        // END is the deadline, not the moment the last pilot got out: at RunLimit the ship and the pod are gone.
        Assert.Equal((Anchor + AbyssalSpace.RunLimit).ToLocalTime().ToString("HH:mm:ss"), model.EndText);
    }

    [Fact]
    public void AbyssalClock_TurnsAmberAtFiveMinutes_ThenRedAtTwo_AndStaysRedPastTheDeadline()
    {
        var model = _Filled(ActivityKind.Abyssal);

        model.Refresh(Anchor.AddMinutes(15).AddSeconds(30));
        Assert.True(model.IsClockWarning);
        Assert.False(model.IsClockCritical);

        model.Refresh(Anchor.AddMinutes(18).AddSeconds(30));
        Assert.False(model.IsClockWarning);
        Assert.True(model.IsClockCritical);

        // Past the deadline we are already wrong about something. A lifted null comparison would have reported that
        // in the resting colour, which is the one state this readout must never be quiet about.
        model.Refresh(Anchor.AddMinutes(21));
        Assert.Equal("--:--", model.ClockText);
        Assert.True(model.IsClockCritical);
    }

    [Fact]
    public void AbyssalClock_MatchesTheCountdownAbyssalSpaceDescribes()
    {
        var model = _Filled(ActivityKind.Abyssal);
        DateTime now = Anchor.AddMinutes(7).AddSeconds(13);
        model.Refresh(now);

        // One countdown, not two. The window shows the figure without Describe's wrapper and without its "+" (the
        // sign moved to the hint under it), so the two must not be able to drift apart on the number itself.
        Assert.Equal($"Abyssal ({model.ClockText}+)", AbyssalSpace.Describe(null, Anchor, now));
    }

    [Fact]
    public void AbyssalClock_WithNoAnchorYet_SaysSoRatherThanShowingAFullRun()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());
        model.Refresh(Anchor);

        Assert.Equal("--:--", model.ClockText);
        Assert.Equal("not started", model.StartText);
        Assert.Equal("not started", model.EndText);
    }

    [Fact]
    public void SiteClock_CountsUp_AndHasNoDeadline()
    {
        var model = _Filled(ActivityKind.Site);
        model.Refresh(Anchor.AddMinutes(73).AddSeconds(4));

        Assert.Equal("ELAPSED", model.ClockLabel);
        Assert.Equal("73:04", model.ClockText);   // past the hour rather than wrapping — a site is bounded by nothing
        Assert.Equal("still running", model.EndText);
    }

    // ── AC-3 — weather and tier, in two clicks, remembered ──────────────────────────────────────────

    [AvaloniaFact]
    public async Task WeatherAndTier_SurviveANewWindow()
    {
        using var instance = TestClientInstance.Create();

        var first = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await first.LoadAsync();
        Assert.Null(first.WeatherIndex);
        Assert.Null(first.TierIndex);

        await first.SelectWeatherCommand.ExecuteAsync(3);
        await first.SelectTierCommand.ExecuteAsync(5);

        var second = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await second.LoadAsync();

        Assert.Equal(3, second.WeatherIndex);
        Assert.Equal(5, second.TierIndex);
        Assert.Equal("Firestorm", second.Weather?.Name);
        Assert.True(second.WeatherChoices[3].IsSelected);
        Assert.True(second.TierChoices[5].IsSelected);

        await second.ClearWeatherAndTierCommand.ExecuteAsync(null);

        var third = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await third.LoadAsync();
        Assert.Null(third.WeatherIndex);
        Assert.Null(third.TierIndex);
    }

    [AvaloniaFact]
    public async Task ARememberedChoiceThatNoLongerAddressesAnything_ReadsAsUnset()
    {
        using var instance = TestClientInstance.Create();
        var settings = instance.Services.GetRequiredService<ISettingRepository>();
        await settings.UpsertAsync(ActivityWindowViewModel.WeatherSettingKey, "9");
        await settings.UpsertAsync(ActivityWindowViewModel.TierSettingKey, "not a number");

        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await model.LoadAsync();

        Assert.Null(model.WeatherIndex);
        Assert.Null(model.TierIndex);
    }

    [Fact]
    public void TheClockDoesNotWaitForWeatherOrTier()
    {
        DateTime now = Anchor.AddMinutes(4).AddSeconds(21);

        var unset = _Filled(ActivityKind.Abyssal);
        unset.Refresh(now);

        var set = _Filled(ActivityKind.Abyssal);
        set.WeatherIndex = 1;
        set.TierIndex = 2;
        set.Refresh(now);

        Assert.Equal(set.ClockText, unset.ClockText);
        Assert.Equal(set.EndText, unset.EndText);

        // And the header asks for the two rather than leaving the reader to notice the gap.
        Assert.True(unset.NeedsWeatherAndTier);
        Assert.False(set.NeedsWeatherAndTier);
    }

    [Fact]
    public void ThePickerFoldsAwayOnceAnswered_AndReopensOnRequest()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());
        Assert.True(model.IsPickerShown);

        model.WeatherIndex = 1;
        Assert.True(model.IsPickerShown);   // half an answer is not an answer

        model.TierIndex = 2;
        Assert.False(model.IsPickerShown);

        model.OpenPickerCommand.Execute(null);
        Assert.True(model.IsPickerShown);
    }

    [Fact]
    public void ThePickerOffersFiveWeathersAndSevenTiers()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        Assert.Equal(5, model.WeatherChoices.Count);
        Assert.Equal(7, model.TierChoices.Count);
        Assert.All(model.WeatherChoices, choice => Assert.False(string.IsNullOrWhiteSpace(choice.Tooltip)));
    }

    [Fact]
    public void ThePenaltyIsShownAsTheBandItRollsIn_NotAsANumberPerTier()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused()) { WeatherIndex = 4, TierIndex = 2 };
        Assert.Contains("-30% or -50%", model.WeatherEffectText);

        model.TierIndex = 5;
        Assert.Contains("-50% or -70%", model.WeatherEffectText);
    }

    // ── AC-6 — the ISK figures name their own source, and claim nothing else ────────────────────────

    [Theory]
    [InlineData(ActivityKind.Abyssal)]
    [InlineData(ActivityKind.Site)]
    public void NoLabelEverImpliesTheWindowValuedTheLootItself(ActivityKind kind)
    {
        var model = _Filled(kind);
        model.WeatherIndex = 2;
        model.TierIndex = 3;
        model.Refresh(Anchor.AddMinutes(3));

        string[] forbidden = ["jita", "markt", "market", "waardering", "appraisal"];
        foreach (var text in _ExposedText(model))
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("clipboard column", model.IskLabel, StringComparison.OrdinalIgnoreCase);
    }

    // ── AC-7 — the four faction palettes reach this window ──────────────────────────────────────────

    [Fact]
    public void EveryBrushInTheWindowIsAResourceKey_NotALiteral()
    {
        // An OverlayWindow does not bind its own brushes to resource observables the way a ChromedWindow does, so one
        // "#rrggbb" left in this file is one thing that silently stops following the faction — and looks perfectly
        // correct in a screenshot of the default palette.
        string markup = File.ReadAllText(_SourcePath("EveUtils.Client/Views/ActivityWindow.axaml"));

        Assert.DoesNotContain("=\"#", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFactionKeyInTheWindowIsBoundLate_AndEveryStaticOneIsNeutral()
    {
        // The counterproof the render below cannot give on its own. The accent reaches the screen through shared
        // theme classes too, so "the accent is on the window" stays true even after a brush in this file has been
        // pinned to whichever palette happened to be loaded when it parsed. This reads the rule instead: a key that
        // differs per faction must be bound late, and a key bound once must not be one of those.
        string markup = File.ReadAllText(_SourcePath("EveUtils.Client/Views/ActivityWindow.axaml"));
        var swappable = _FactionKeys();

        foreach (var key in _Keys(markup, "StaticResource"))
            Assert.False(swappable.Contains(key),
                $"{key} differs per faction, so StaticResource freezes it at whichever palette parsed first");

        foreach (var key in _Keys(markup, "DynamicResource"))
            Assert.True(swappable.Contains(key),
                $"{key} is bound late but is not in Themes/Factions — it is either a typo or a neutral key");
    }

    [AvaloniaFact]
    public void TheAccentOnScreen_IsTheAccentOfTheAppliedFaction()
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();

        (FactionTheme Faction, Color Accent)[] palettes =
        [
            (FactionTheme.Gallente, Color.Parse("#FF7EE0BB")),
            (FactionTheme.Amarr, Color.Parse("#FFF3D488")),
            (FactionTheme.Caldari, Color.Parse("#FF8FC6F0")),
            (FactionTheme.Minmatar, Color.Parse("#FFE68676"))
        ];

        try
        {
            foreach (var (faction, accent) in palettes)
            {
                theme.Apply(faction);

                var window = _Open(_Set(_Running(ActivityKind.Abyssal)), expanded: true);
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);

                var painted = _Colours(frame!);
                Assert.True(painted.Contains(accent),
                    $"{faction}'s accent is nowhere on the window — it is still wearing another palette");

                foreach (var (other, otherAccent) in palettes.Where(palette => palette.Faction != faction))
                    Assert.False(painted.Contains(otherAccent),
                        $"{other}'s accent is still on screen after applying {faction}");

                OverlayShots.Capture(window, $"eveutils-activity-{faction}".ToLowerInvariant());
                window.Close();
            }
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    // ── The window itself ───────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TheWindowRenders_FoldedShut_Open_AndStillAsking()
    {
        var model = _Set(_Running(ActivityKind.Abyssal));

        var shut = _Open(model, expanded: false);
        Assert.NotNull(shut.CaptureRenderedFrame());
        OverlayShots.Capture(shut, "eveutils-activity-shut");
        shut.Close();

        var open = _Open(model, expanded: true);
        Assert.NotNull(open.CaptureRenderedFrame());
        OverlayShots.Capture(open, "eveutils-activity-open");
        open.Close();

        // The state the window opens in on a fresh run: the clock already running, the header asking for the two
        // things nothing can detect for it.
        var asking = _Open(_Running(ActivityKind.Abyssal), expanded: true);
        Assert.NotNull(asking.CaptureRenderedFrame());
        OverlayShots.Capture(asking, "eveutils-activity-unset");
        asking.Close();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A provider nothing in these tests reaches into: only the setting round-trip touches the client DI,
    /// and it uses a real <see cref="TestClientInstance"/>.</summary>
    private static IServiceProvider _Unused() => new ServiceCollection().BuildServiceProvider();

    private static ActivityWindowViewModel _Filled(ActivityKind kind) => _Filled(kind, Anchor);

    private static ActivityWindowViewModel _Filled(ActivityKind kind, DateTime anchorUtc) =>
        new(kind, _Unused())
        {
            AnchorUtc = anchorUtc,
            SolarSystem = kind == ActivityKind.Site ? "Aphend" : null,   // a pocket genuinely has none
            LootStrategy = "Bioadaptive + some cans"
        };

    /// <summary>A run six minutes in on the clock the window itself will read. Anything anchored to a fixed date
    /// renders as a full twenty minutes, because Show() starts the timer and the timer uses the real now.</summary>
    private static ActivityWindowViewModel _Running(ActivityKind kind) =>
        _Filled(kind, DateTime.UtcNow.AddMinutes(-6));

    private static ActivityWindowViewModel _Set(ActivityWindowViewModel model)
    {
        model.WeatherIndex = 4;
        model.TierIndex = 3;
        model.Refresh(DateTime.UtcNow);
        return model;
    }

    private static ActivityWindow _Open(ActivityWindowViewModel model, bool expanded)
    {
        foreach (var section in model.Sections)
            section.IsExpanded = expanded;

        var window = new ActivityWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Every colour the frame actually contains. Exact matches only: an accent that is on screen is on
    /// screen at full strength somewhere, and a softened or antialiased near-miss proves nothing either way.</summary>
    private static HashSet<Color> _Colours(Bitmap frame)
    {
        var area = new PixelRect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height);
        var pixels = new byte[area.Width * area.Height * 4];
        frame.CopyPixels(area, Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0), pixels.Length, area.Width * 4);

        // The headless Skia backend hands these over as Rgba8888, not the Bgra8888 the rest of the world assumes.
        // Read the channels the wrong way round and every colour comes out as a plausible-looking different colour,
        // which is the one failure mode a colour assertion cannot survive.
        bool rgba = frame.Format == PixelFormat.Rgba8888;

        var colours = new HashSet<Color>();
        for (var i = 0; i < pixels.Length; i += 4)
            colours.Add(rgba
                ? Color.FromArgb(pixels[i + 3], pixels[i], pixels[i + 1], pixels[i + 2])
                : Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]));

        return colours;
    }

    /// <summary>Every piece of text this view model puts on screen. Gathered by reflection rather than from a
    /// hand-kept list, so a label added later cannot slip past the rule above it.</summary>
    private static IEnumerable<string> _ExposedText(ActivityWindowViewModel model) =>
        typeof(ActivityWindowViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            .Select(property => property.GetValue(model) as string)
            .OfType<string>()
            .Concat(model.Sections.Select(section => section.Title))
            .Concat(model.Sections.Select(section => section.HeaderSummary))
            .Concat(model.WeatherChoices.Select(choice => choice.Tooltip).OfType<string>());

    /// <summary>Every resource key the markup asks for under one of the two markup extensions.</summary>
    private static IEnumerable<string> _Keys(string markup, string extension) =>
        Regex.Matches(markup, @"\{" + extension + @"\s+([A-Za-z0-9_]+)\s*\}")
            .Select(match => match.Groups[1].Value)
            .Distinct();

    /// <summary>The keys that actually change with the faction — the ones every one of the four palettes defines.
    /// Read from the palettes rather than listed here, so a key added to them is covered without a second edit.</summary>
    private static HashSet<string> _FactionKeys()
    {
        List<HashSet<string>> perFaction = Enum.GetNames<FactionTheme>()
            .Select(faction => _SourcePath($"EveUtils.Client/Themes/Factions/{faction}.axaml"))
            .Select(path => Regex.Matches(File.ReadAllText(path), @"x:Key=""([A-Za-z0-9_]+)""")
                .Select(match => match.Groups[1].Value)
                .ToHashSet())
            .ToList();

        var shared = perFaction[0];
        foreach (var keys in perFaction.Skip(1))
            shared.IntersectWith(keys);

        return shared;
    }

    /// <summary>The repository file, found from the test binary rather than from a checkout path baked in here.</summary>
    private static string _SourcePath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            relative);
    }
}
