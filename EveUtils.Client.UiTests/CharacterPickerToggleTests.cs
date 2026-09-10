using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-186: in the multi-select picker, SelectionMode.Multiple alone only allows more than one row to be selected —
/// it says nothing about how a click behaves, so a plain click replaced the whole selection instead of adding to
/// it. These drive real pointer clicks through the headless input pipeline (not just asserting the property that
/// was set) so they measure what a click does, not just that the window opens. FleetInviteWindow's picker is
/// covered too even though its list is fixed single-select (ET-184 pulled it into the same row template) — to
/// confirm that path was never affected rather than assume it.
/// </summary>
public class CharacterPickerToggleTests
{
    private static readonly IReadOnlyList<CharacterPickOption> ThreeCharacters =
    [
        new(1, "Alpha", "", Enabled: true),
        new(2, "Bravo", "", Enabled: true),
        new(3, "Charlie", "", Enabled: true),
    ];

    private static Point CentreOf(ListBox list, Window window, int index)
    {
        var container = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(index));
        return container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"row {index} is not in the tree");
    }

    // Slept past the platform's double-tap window: two of these in a row on the same row would otherwise register
    // as a double-tap and confirm the dialog (OnConfirm's real, correct behaviour) instead of the two separate
    // clicks a test like Single_ClickMovesTheChoice_AndNeverGoesEmpty means to drive.
    private static void Click(Window window, ListBox list, int index, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = CentreOf(list, window, index);
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(600);
    }

    private static (CharacterPickerWindow Window, ListBox List) ShowPicker(bool multiSelect, IReadOnlyList<CharacterPickOption>? options = null)
    {
        var window = new CharacterPickerWindow("Pick", options ?? ThreeCharacters, multiSelect);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("OptionList")!;
        return (window, list);
    }

    [AvaloniaFact]
    public void MultiSelect_PlainClicksAddEachRow_NoModifierNeeded()
    {
        var (window, list) = ShowPicker(multiSelect: true);

        Click(window, list, 0);
        Click(window, list, 1);

        var selected = list.SelectedItems!.Cast<CharacterPickRowViewModel>().Select(r => r.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Alpha", "Bravo" }, selected);

        window.Close();
    }

    [AvaloniaFact]
    public void MultiSelect_ClickingASelectedRowAgain_TogglesOnlyThatRowOff()
    {
        var (window, list) = ShowPicker(multiSelect: true);

        Click(window, list, 0);
        Click(window, list, 1);
        Click(window, list, 0); // toggle Alpha back off

        var selected = list.SelectedItems!.Cast<CharacterPickRowViewModel>().Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "Bravo" }, selected);

        window.Close();
    }

    [AvaloniaFact]
    public void MultiSelect_CtrlAndShiftClicks_KeepWorkingAlongsideThePlainToggle()
    {
        var (window, list) = ShowPicker(multiSelect: true);

        Click(window, list, 0);
        Click(window, list, 2, RawInputModifiers.Control);

        var selected = list.SelectedItems!.Cast<CharacterPickRowViewModel>().Select(r => r.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Alpha", "Charlie" }, selected);

        window.Close();
    }

    [AvaloniaFact]
    public void MultiSelect_DisabledRowStaysUnselectable()
    {
        IReadOnlyList<CharacterPickOption> options =
        [
            new(1, "Alpha", "", Enabled: true),
            new(2, "Bravo", "needs re-auth", Enabled: false),
        ];
        var (window, list) = ShowPicker(multiSelect: true, options);

        Click(window, list, 1);

        Assert.Empty(list.SelectedItems!.Cast<object>());
        window.Close();
    }

    [AvaloniaFact]
    public void Single_ClickMovesTheChoice_AndNeverGoesEmpty()
    {
        var (window, list) = ShowPicker(multiSelect: false);

        Click(window, list, 0);
        Assert.Same(window.Options[0], list.SelectedItem);

        Click(window, list, 1);
        Assert.Same(window.Options[1], list.SelectedItem);

        Click(window, list, 1); // clicking the current choice again must not clear it
        Assert.Same(window.Options[1], list.SelectedItem);

        window.Close();
    }

    // ET-207: this dialog fires from a ctrl+c on a combat site while EVE is fullscreen and no activity window is
    // open yet, so DialogService's own "raise it over the run overlay" rule never applies to this case — the window
    // has to own always-on-top itself instead of depending on it.
    [AvaloniaFact]
    public void AlwaysOpensTopmost_EvenWithNoActivityWindowUp()
    {
        var (window, _) = ShowPicker(multiSelect: false);

        Assert.True(window.Topmost);

        window.Close();
    }

    // ET-216: the character whose own clipboard copy raised the question starts ticked — a starting point the
    // pilot can still add to or clear, never an automatic choice.
    [AvaloniaFact]
    public void PreselectedCharacterId_TicksThatRowInTheMultiSelectList()
    {
        var window = new CharacterPickerWindow("Pick", ThreeCharacters, multiSelect: true, preselectedCharacterId: 2);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("OptionList")!;

        var selectedNames = list.SelectedItems!.Cast<CharacterPickRowViewModel>().Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "Bravo" }, selectedNames);
        Assert.True(window.Options.Single(o => o.Name == "Bravo").IsSelected);
        Assert.False(window.Options.Single(o => o.Name == "Alpha").IsSelected);

        window.Close();
    }

    // ET-216 AC-4: the sender's own option can be disabled (no longer among the connected characters) — no
    // preselection, and no error, same as before this ticket.
    [AvaloniaFact]
    public void PreselectedCharacterId_DisabledRow_IsIgnored()
    {
        IReadOnlyList<CharacterPickOption> options =
        [
            new(1, "Alpha", "", Enabled: true),
            new(2, "Bravo", "needs re-auth", Enabled: false),
        ];
        var window = new CharacterPickerWindow("Pick", options, multiSelect: true, preselectedCharacterId: 2);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("OptionList")!;

        Assert.Empty(list.SelectedItems!.Cast<object>());

        window.Close();
    }

    // ET-216 AC-4: the sender is not among the candidates shown at all — same "nothing to preselect" outcome.
    [AvaloniaFact]
    public void PreselectedCharacterId_NotAmongOptions_IsIgnored()
    {
        IReadOnlyList<CharacterPickOption> options = [new(1, "Alpha", "", Enabled: true)];
        var window = new CharacterPickerWindow("Pick", options, multiSelect: true, preselectedCharacterId: 999);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("OptionList")!;

        Assert.Empty(list.SelectedItems!.Cast<object>());

        window.Close();
    }

    [AvaloniaFact]
    public void FleetInviteWindow_FixedSingleSelectPath_IsUnaffected()
    {
        var window = new FleetInviteWindow("Home Fleet", ThreeCharacters);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("OptionList")!;

        Click(window, list, 0);
        Assert.Same(window.Options[0], list.SelectedItem);

        Click(window, list, 1);
        Assert.Same(window.Options[1], list.SelectedItem);

        window.Close();
    }
}
