using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Input;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The tab-selection arithmetic behind Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+1..9 / Ctrl+9 (ET-209), pulled out of
/// <c>MainWindow</c>'s key handling so it can be driven directly without a window or key events.
/// </summary>
public class TabCyclingTests
{
    private static HostTab NewTab(string title) =>
        new() { Content = new Border(), Title = title, CloseCommand = new RelayCommand(() => { }) };

    [AvaloniaFact]
    public void Next_WrapsPastTheLastTab()
    {
        var a = NewTab("A");
        var b = NewTab("B");
        var c = NewTab("C");
        var tabs = new[] { a, b, c };

        Assert.Same(b, TabCycling.Next(tabs, a));
        Assert.Same(c, TabCycling.Next(tabs, b));
        Assert.Same(a, TabCycling.Next(tabs, c));   // wraps
    }

    [AvaloniaFact]
    public void Previous_WrapsPastTheFirstTab()
    {
        var a = NewTab("A");
        var b = NewTab("B");
        var c = NewTab("C");
        var tabs = new[] { a, b, c };

        Assert.Same(a, TabCycling.Previous(tabs, b));
        Assert.Same(c, TabCycling.Previous(tabs, a));   // wraps
        Assert.Same(b, TabCycling.Previous(tabs, c));
    }

    [AvaloniaFact]
    public void NoSelection_NextGoesToFirst_PreviousGoesToLast()
    {
        var tabs = new[] { NewTab("A"), NewTab("B"), NewTab("C") };

        Assert.Same(tabs[0], TabCycling.Next(tabs, null));
        Assert.Same(tabs[2], TabCycling.Previous(tabs, null));
    }

    [AvaloniaFact]
    public void NoTabs_EverythingReturnsNull()
    {
        var none = System.Array.Empty<HostTab>();
        Assert.Null(TabCycling.Next(none, null));
        Assert.Null(TabCycling.Previous(none, null));
        Assert.Null(TabCycling.At(none, 1));
        Assert.Null(TabCycling.Last(none));
    }

    [AvaloniaFact]
    public void At_IsOneBased_AndNullBeyondTheCount()
    {
        var tabs = new[] { NewTab("A"), NewTab("B") };

        Assert.Same(tabs[0], TabCycling.At(tabs, 1));   // Ctrl+1
        Assert.Same(tabs[1], TabCycling.At(tabs, 2));   // Ctrl+2
        Assert.Null(TabCycling.At(tabs, 3));            // Ctrl+3 with only two tabs open
        Assert.Null(TabCycling.At(tabs, 0));
    }

    [AvaloniaFact]
    public void Last_IsTheLastTab_RegardlessOfCount()
    {
        var tabs = new[] { NewTab("A"), NewTab("B"), NewTab("C") };
        Assert.Same(tabs[2], TabCycling.Last(tabs));   // Ctrl+9, browser convention — not "the 9th tab"
    }
}
