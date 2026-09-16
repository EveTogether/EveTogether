using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// TYPES or CHARACTERS (ET-293): a label, a <see cref="ColumnFlowPanel"/> of tiles, and — only while something in it
/// is off — an <c>n of m</c> count and a SHOW ALL. Everything on is the default and needs no header text at all: a
/// block that never has anything to say stays quiet rather than showing "11 of 11" on every screen it is opened on.
/// </summary>
public sealed partial class RunFilterBlockViewModel : ObservableObject
{
    private readonly Action _showAll;

    public RunFilterBlockViewModel(string label, Action showAll)
    {
        Label = label;
        _showAll = showAll;
    }

    public string Label { get; }

    public ObservableCollection<RunFilterTileViewModel> Tiles { get; } = [];

    /// <summary>"6 of 7", or null while every tile in this block is on — <see cref="HasSummary"/> is what the view
    /// hides the whole header line on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summaryText;

    public bool HasSummary => SummaryText is not null;

    [RelayCommand]
    private void ShowAll() => _showAll();
}
