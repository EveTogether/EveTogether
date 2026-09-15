using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>
/// The runs screen (ET-161), hosted like the other feature modules: a docked tab when docked, its own window when
/// floating — the same <c>Content</c> either way, which is why the layout sizes off the space it is given.
///
/// Not an <c>IHostableModuleWindow</c>: the screen carries no close button of its own, so a docked tab is closed by
/// its own X and a floating one by the chrome's. The view-model is disposed on close because its running clock is a
/// timer, and a timer that outlives its window keeps ticking for the rest of the session.
/// </summary>
public partial class RunsWindow : ChromedWindow
{
    private readonly ListBox _activityList;
    private readonly Border _stickyDay;
    private readonly ContentControl _stickyDayContent;

    public RunsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _activityList = this.FindControl<ListBox>("ActivityList")!;
        _stickyDay = this.FindControl<Border>("StickyDay")!;
        _stickyDayContent = this.FindControl<ContentControl>("StickyDayContent")!;
        _activityList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => _PinTopDay());
        // Folding the day from its pinned header brings that header to the top, rather than leaving the reader
        // wherever the rows after it happen to land.
        _stickyDay.AddHandler(Button.ClickEvent, (_, _) => Dispatcher.UIThread.Post(_ScrollPinnedDayIntoView),
            handledEventsToo: true);
    }

    public RunsWindow(RunsOverviewViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
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
