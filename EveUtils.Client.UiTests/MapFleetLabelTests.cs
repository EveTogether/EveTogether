using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Controls.Map;
using Xunit;
using static EveUtils.Client.UiTests.MapFollowFleetTests;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-403: the label beside a system's markers stays one short line however many characters stand there, and the
/// system popover of ET-399 names them all (or as many as fit the map).</summary>
public sealed class MapFleetLabelTests
{
    [Theory]
    [InlineData(1, "Pilot01")]
    [InlineData(2, "Pilot01, Pilot02")]
    [InlineData(5, "Pilot01 +4")]
    [InlineData(30, "Pilot01 +29")]
    public void Label_ByHeadcount_IsNamesThenLeadPlusTheRest(int count, string expected)
    {
        Assert.Equal(expected, MapMarkerLabel.For(_Names(count), null));
    }

    [Fact]
    public void Label_WithTheCommanderAmongThem_LeadsWithTheCommander()
    {
        Assert.Equal("Pilot04 +4", MapMarkerLabel.For(_Names(5), "Pilot04"));
    }

    [Fact]
    public void Label_WithACommanderWhoIsNotAmongThem_LeadsWithTheFirst()
    {
        Assert.Equal("Pilot01 +2", MapMarkerLabel.For(_Names(3), "Somebody Else"));
    }

    [AvaloniaFact]
    public async Task TheLabel_At5And30Members_StaysShortAndAsWide()
    {
        using var world = await World.OpenAsync();
        (_, StarMapControl map) = world.Show();
        int jita = world.IndexOf(Jita), amarr = world.IndexOf(Amarr);
        world.Model.Markers =
        [
            .. _Names(5).Select(name => new MapMarker(jita, name)),
            .. _Names(30).Select(name => new MapMarker(amarr, name))
        ];
        UiDispatcher.UIThread.RunJobs();

        Size five = map.MarkerLabelSizeOf(jita), thirty = map.MarkerLabelSizeOf(amarr);

        Assert.Equal("Pilot01 +4", map.MarkerLabelOf(jita));
        Assert.Equal("Pilot01 +29", map.MarkerLabelOf(amarr));
        Assert.InRange(five.Width, 10, 110);
        Assert.InRange(thirty.Width - five.Width, 0, 9);
        Assert.Equal(five.Height, thirty.Height);
    }

    [AvaloniaFact]
    public async Task TheLabel_OfAGroupWithTheFleetCommander_LeadsWithTheCommander()
    {
        using var world = await World.OpenAsync();
        (_, StarMapControl map) = world.Show();
        int jita = world.IndexOf(Jita);
        world.Model.Markers = [.. _Names(4).Select(name => new MapMarker(jita, name))];
        world.Model.FleetBadges = [new MapFleetBadge(jita, [new MapFleetSighting("Pilot03", T0, true), new MapFleetSighting("Pilot01", T0, false)])];
        UiDispatcher.UIThread.RunJobs();

        Assert.Equal("Pilot03 +3", map.MarkerLabelOf(jita));
    }

    [AvaloniaFact]
    public async Task HoveringTheMarker_OpensTheSystemPopover_WithTheCommanderFirstThenAlphabetical()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita);
        world.Model.Markers = [.. _Names(5).Select(name => new MapMarker(jita, name))];
        world.Model.FleetBadges = [new MapFleetBadge(jita, [.. _Names(5).Select(name => new MapFleetSighting(name, T0, name == "Pilot04"))])];
        _Focus(map, jita, 11);

        _HoverAt(window, map, map.MarkerPointOf(jita));

        Assert.Equal(jita, map.PopoverIndex);
        string[] rows = map.PopoverText!.Split('\n');
        Assert.StartsWith("Jita ", rows[0]);
        int here = Array.IndexOf(rows, "Here · 5");
        Assert.True(here > 0, "no members section in the system popover");
        Assert.Equal(
            ["FC · Pilot04 · 0s ago", "Pilot01 · 0s ago", "Pilot02 · 0s ago", "Pilot03 · 0s ago", "Pilot05 · 0s ago"],
            rows.Skip(here + 1).Take(5));
    }

    [AvaloniaFact]
    public async Task ASystemWithoutMembers_HasNoMembersSection()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita);
        _Focus(map, jita, 11);

        _HoverAt(window, map, map.ScreenPointOf(jita));

        Assert.DoesNotContain(map.PopoverText!.Split('\n'), row => row.StartsWith("Here ·"));
    }

    [AvaloniaFact]
    public async Task With30Members_ThePopoverNamesFifteenAndStaysInsideTheMap()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita);
        world.Model.FleetBadges = [new MapFleetBadge(jita, [.. _Names(30).Select(name => new MapFleetSighting(name, T0, name == "Pilot30"))])];
        _Focus(map, jita, 11);

        _HoverAt(window, map, map.ScreenPointOf(jita));

        string[] rows = map.PopoverText!.Split('\n');
        Assert.Equal("Here · 30", rows.Single(row => row.StartsWith("Here ·")));
        Assert.Equal("FC · Pilot30 · 0s ago", rows[Array.IndexOf(rows, "Here · 30") + 1]);
        Assert.Contains("+15 more here", rows);
        _AssertInside(map);
    }

    [AvaloniaFact]
    public async Task InAShortMap_ThePopoverNamesFewerMembersRatherThanLeaveTheMap()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita);
        world.Model.FleetBadges = [new MapFleetBadge(jita, [.. _Names(30).Select(name => new MapFleetSighting(name, T0, false))])];
        _Focus(map, jita, 11);
        _HoverAt(window, map, map.ScreenPointOf(jita));
        string[] tall = map.PopoverText!.Split('\n');

        window.Height = map.Bounds.Height - map.PopoverBounds.Height / 2;
        UiDispatcher.UIThread.RunJobs();
        MapFollowTests.Settle();
        _HoverAt(window, map, map.ScreenPointOf(jita));

        string[] short_ = map.PopoverText!.Split('\n');
        Assert.True(short_.Length < tall.Length, $"{short_.Length} rows in the short map, {tall.Length} in the tall one");
        _AssertInside(map);
    }

    private static void _AssertInside(StarMapControl map)
    {
        Rect popover = map.PopoverBounds;
        Assert.True(popover.Width > 0 && popover.Height > 0, "no popover");
        Assert.True(popover.X >= 0 && popover.Y >= 0 && popover.Right <= map.Bounds.Width && popover.Bottom <= map.Bounds.Height,
            $"popover {popover} outside the map {map.Bounds.Size}");
    }

    private static List<string> _Names(int count) => [.. Enumerable.Range(1, count).Select(number => $"Pilot{number:00}")];

    private static (Window, StarMapControl) _Open(World world)
    {
        (Window window, StarMapControl map) = world.Show();
        map.PopoverDelay = TimeSpan.Zero;
        return (window, map);
    }

    private static void _Focus(StarMapControl map, int systemIndex, double zoom)
    {
        map.FocusRequest = new MapFocusRequest([systemIndex], zoom);
        MapFollowTests.Settle();
    }

    private static void _HoverAt(Window window, StarMapControl map, Point inMap)
    {
        Point at = map.TranslatePoint(inMap, window) ?? default;
        window.MouseMove(new Point(at.X + 1, at.Y));
        window.MouseMove(at);
        UiDispatcher.UIThread.RunJobs();
    }
}
