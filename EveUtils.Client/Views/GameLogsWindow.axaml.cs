using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EveUtils.Client.ViewModels.GameLogs;

namespace EveUtils.Client.Views;

/// <summary>
/// The GAME LOGS screen (ET-410), hosted like KILLMAILS: a docked tab when docked, its own window when floating. The
/// list follows the newest line while it is scrolled to the bottom, and stays put once the pilot scrolls up to read.
/// </summary>
public partial class GameLogsWindow : ChromedWindow
{
    private const double BottomTolerance = 4;

    private ScrollViewer? _scroller;
    private bool _followingNewest = true;

    public GameLogsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ItemsControl>("LinesList")?.AddHandler(ScrollViewer.ScrollChangedEvent, _OnScrollChanged);
    }

    public GameLogsWindow(GameLogsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Rows.CollectionChanged += _OnRowsChanged;
        Closed += (_, _) =>
        {
            viewModel.Rows.CollectionChanged -= _OnRowsChanged;
            viewModel.Dispose();
        };
    }

    private void _OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scroller)
            return;

        _scroller = scroller;
        _followingNewest = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - BottomTolerance;
    }

    // A swapped list (a filter, a period) starts at the newest line again; an appended line only pulls along a list
    // that was already at the bottom.
    private void _OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
            _followingNewest = true;

        if (_followingNewest)
            Dispatcher.UIThread.Post(() => _scroller?.ScrollToEnd(), DispatcherPriority.Loaded);
    }
}
