using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>
/// The runs screen (ET-161), hosted like the other feature modules: a docked tab when docked, its own window when
/// floating — the same <c>Content</c> either way, which is why the layout sizes off the space it is given.
///
/// Not an <c>IHostableModuleWindow</c>: the screen carries no close button of its own, so a docked tab is closed by
/// its own X and a floating one by the chrome's. The view-model is disposed on close because its running clock is a
/// timer, and a timer that outlives its window keeps ticking for the rest of the session.
///
/// <para>What is here rather than in the view model (ET-291): the width the content root was handed, the pane's own
/// column width that follows from it, the drawer's ceiling, the keys, and giving focus somewhere sensible when the
/// drawer opens and closes. All of it is about controls, and the view model is asked the questions instead of being
/// told the answers.</para>
/// </summary>
public partial class RunsWindow : ChromedWindow
{
    private readonly Grid _root;
    private readonly ListBox _activityList;
    private readonly Border _stickyDay;
    private readonly ContentControl _stickyDayContent;
    private readonly ColumnDefinition _paneColumn;
    private readonly Border _drawer;
    private readonly Button _drawerClose;

    public RunsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _root = this.FindControl<Grid>("RunsRoot")!;
        _activityList = this.FindControl<ListBox>("ActivityList")!;
        _stickyDay = this.FindControl<Border>("StickyDay")!;
        _stickyDayContent = this.FindControl<ContentControl>("StickyDayContent")!;
        _paneColumn = this.FindControl<Grid>("ListAndPane")!.ColumnDefinitions[1];
        _drawer = this.FindControl<Border>("Drawer")!;
        _drawerClose = this.FindControl<Button>("DrawerClose")!;
        _activityList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => _PinTopDay());
        // Folding the day from its pinned header brings that header to the top, rather than leaving the reader
        // wherever the rows after it happen to land.
        _stickyDay.AddHandler(Button.ClickEvent, (_, _) => Dispatcher.UIThread.Post(_ScrollPinnedDayIntoView),
            handledEventsToo: true);

        // The width the content root reports — the module host's column when docked, the window when floating.
        // Subscribed on the root and not on the window because the host lifts the root out of the window (ET-42):
        // a subscription here follows the content wherever it is parented. The observer reads the root's own
        // DataContext on every tick rather than capturing one, since ModuleHostService.Open re-assigns it.
        _root.GetObservable(BoundsProperty).Subscribe(new WidthObserver(this));

        // The detail screen is a double-click, never a second single click: the first click has already selected
        // the row and, on the narrow layout, opened the drawer — a counter of presses would close it again.
        _activityList.AddHandler(DoubleTappedEvent, _OnRowDoubleTapped, handledEventsToo: true);

        // One handler for the whole screen, tunnelling so it settles ↑↓ before the list's own navigation does and
        // Esc before anything a shortcut may have been bound to. Docked content gets no keys at all unless the
        // focus is inside it, which is why opening the drawer moves the focus into it.
        _root.AddHandler(KeyDownEvent, _OnKeyDown, RoutingStrategies.Tunnel);
    }

    public RunsWindow(RunsOverviewViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.PropertyChanged += _OnViewModelPropertyChanged;
        viewModel.DayScrollRequested += _ScrollDayToTop;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= _OnViewModelPropertyChanged;
            viewModel.DayScrollRequested -= _ScrollDayToTop;
            viewModel.Dispose();
        };
    }

    /// <summary>
    /// A day picked in the activity strip (ET-292), at the top of the list. The list is virtualised and the day was
    /// only just unfolded, so its header may not have a container yet: first the layout that takes the unfold in, then
    /// <c>ScrollIntoView</c> to realise the header somewhere in view, then the offset moved by exactly how far below
    /// the top it landed. At the top, the pinned day over the list is that very day drawn over its own header.
    /// </summary>
    private void _ScrollDayToTop(RunsDayViewModel day) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_activityList.Scroll is not ScrollViewer scroll)
                return;

            _activityList.UpdateLayout();
            _activityList.ScrollIntoView(day);
            _activityList.UpdateLayout();
            if (_activityList.ContainerFromItem(day) is { } header
                && header.TranslatePoint(new Point(0, 0), scroll) is { } at)
            {
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + at.Y));
                _activityList.UpdateLayout();
            }

            _PinTopDay();
        }, DispatcherPriority.Background);

    private RunsOverviewViewModel? _ViewModel => _root.DataContext as RunsOverviewViewModel;

    /// <summary>The width the module content was given, and everything that follows from it: whether the pane stands
    /// beside the list or waits in the drawer, how wide its column is, and how much of the list the drawer leaves
    /// showing at the narrowest widths.</summary>
    private void _ApplyWidth(double width)
    {
        _ViewModel?.ApplyWidth(width);
        bool wide = width >= RunsLayout.WideFrom;
        // Explicit, never Auto (ET-285): the list is the starred column and the pane a fixed one, so a long site
        // name can never take room from the pane or the pane from the list.
        _paneColumn.Width = new GridLength(wide ? RunsLayout.PaneWidth : 0);
        _drawer.MaxWidth = Math.Max(RunsLayout.DrawerWidth / 2, width - RunsLayout.DrawerMinimumGap);
    }

    private void _OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunsOverviewViewModel.IsDrawerOpen))
            Dispatcher.UIThread.Post(_MoveFocusForDrawer);
    }

    /// <summary>Opened, the focus goes to ✕ — without it a docked tab gets no key events at all and Esc would only
    /// work after a click. Closed, it goes back to the row that was selected, or to that row's day header when the
    /// day has since been folded and the row itself is no longer in the list.</summary>
    private void _MoveFocusForDrawer()
    {
        if (_ViewModel is not { } viewModel)
            return;

        if (viewModel.IsDrawerOpen)
        {
            _drawerClose.Focus();
            return;
        }

        if (viewModel.SelectedRow is not { } selected)
            return;

        if (_activityList.ContainerFromItem(selected) is { } container)
        {
            container.Focus();
            return;
        }

        RunsDayViewModel? day = viewModel.SelectedTab?.Days.FirstOrDefault(candidate => candidate.Rows.Contains(selected));
        if (day is not null && _activityList.ContainerFromItem(day) is { } header)
            header.Focus();
    }

    private void _OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_ViewModel is not { } viewModel)
            return;

        if ((e.Source as Control)?.DataContext is ActivityOverviewRowViewModel or ActivityRunRowViewModel)
            _ = viewModel.OpenSelectedDetailAsync();
    }

    /// <summary>
    /// ↑↓ steps through the activity rows of unfolded days; ↵ opens the detail screen, or the drawer where it is shut;
    /// Esc closes the drawer and nothing else. Handled here rather than left to a shortcut binding because a bare Esc
    /// can be bound to another action in Settings, and because the list's own arrow navigation would otherwise walk
    /// day headers and pilots' runs as though they were rows.
    /// </summary>
    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _ViewModel is not { } viewModel)
            return;

        // A typed key belongs to what is being typed into, and the source strip answers its own arrows.
        if (e.Source is TextBox || (e.Source as Control)?.FindAncestorOfType<ListBox>() is { Name: "SourceTabs" })
            return;

        switch (e.Key)
        {
            case Key.Escape when viewModel.IsDrawerOpen:
                viewModel.CloseDrawer();
                break;
            case Key.Down:
                viewModel.MoveSelection(1);
                break;
            case Key.Up:
                viewModel.MoveSelection(-1);
                break;
            case Key.Enter when viewModel.HasSelection:
                _ = viewModel.OpenSelectedDetailAsync();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private sealed class WidthObserver(RunsWindow window) : IObserver<Rect>
    {
        public void OnNext(Rect bounds) => window._ApplyWidth(bounds.Width);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>The sticky day header (ET-290). A virtualised list has no header element to pin — the one that scrolled
    /// away may already be drawing another row — so the day of the topmost visible item is drawn again over the top of
    /// the list, from the same template, while the list is scrolled away from its start.</summary>
    private void _PinTopDay()
    {
        RunsDayViewModel? day = _activityList.Scroll is { Offset.Y: > 0.5 } ? _TopDay() : null;
        _stickyDayContent.Content = day;
        _stickyDay.IsVisible = day is not null;
    }

    private RunsDayViewModel? _TopDay()
    {
        if (_activityList.ItemsSource is not IReadOnlyList<object> items || items.Count == 0)
            return null;

        int top = int.MaxValue;
        foreach (Control container in _activityList.GetRealizedContainers())
        {
            if (!container.IsVisible
                || container.TranslatePoint(new Point(0, container.Bounds.Height), _activityList) is not { Y: > 0 })
                continue;

            int index = _activityList.IndexFromContainer(container);
            if (index >= 0)
                top = Math.Min(top, index);
        }

        for (int index = Math.Min(top, items.Count - 1); index >= 0; index--)
            if (items[index] is RunsDayViewModel day)
                return day;
        return null;
    }

    private void _ScrollPinnedDayIntoView()
    {
        if (_stickyDayContent.Content is RunsDayViewModel day)
            _activityList.ScrollIntoView(day);
        _PinTopDay();
    }
}
