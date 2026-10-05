using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Reading;

namespace EveUtils.Client.ViewModels.GameLogs;

/// <summary>
/// ET-410: the GAME LOGS screen — the game log lines of every character in one chronological list, newest at the
/// bottom, with a type chip row, a character dropdown (ET-405's pattern: "All characters" first), a search box and a
/// period (today by default). It reads what the gamelog watcher already tails through <see cref="IGameLogLineSource"/>;
/// it opens no file of its own for the live part, so nothing is tailed twice and EVE's files are only ever read.
///
/// <para>A line that arrives while the screen is open is queued on the watcher's thread and added on the UI thread in
/// batches of a quarter second — inserted by time, because the watcher reads one character's file after the other and
/// a later poll can carry an earlier second. Filtering never reads again: it picks from the rows of the last read.</para>
///
/// <para>Bounded: the newest <see cref="MaxLines"/> lines are kept, the list is virtualized by the view, and the rows
/// are built once per line.</para>
/// </summary>
public sealed partial class GameLogsViewModel : ViewModelBase, IDisposable
{
    public const int MaxLines = 100_000;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly GameLogLineKind[] Kinds = Enum.GetValues<GameLogLineKind>();

    private readonly IGameLogLineSource _source;
    private readonly TimeProvider _clock;
    private readonly CharacterFaceCache _faces;
    private readonly IReadOnlyDictionary<string, int> _characterIds;
    private readonly ConcurrentQueue<GameLogLine> _pending = new();
    private readonly Dictionary<string, int> _unlinkedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameLogKindChipViewModel[] _chipByKind;

    private List<GameLogRowViewModel> _loaded = [];
    private GameLogLineBuffer? _buffer;
    private int _loadVersion;
    private int _flushScheduled;
    private bool _isLoading;
    private bool _isRebuildingOptions;

    public GameLogsViewModel(IGameLogLineSource source, IReadOnlyList<Character> characters,
        ICharacterPortraitProvider? portraits = null, TimeProvider? clock = null)
    {
        _source = source;
        _clock = clock ?? TimeProvider.System;
        _faces = new CharacterFaceCache(portraits);
        _characterIds = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().EsiCharacterId!.Value, StringComparer.OrdinalIgnoreCase);

        _chipByKind = [.. Kinds.Select(kind => new GameLogKindChipViewModel(kind, _ApplyFilter))];
        KindChips = [.. Kinds.Where(kind => kind != GameLogLineKind.Other).Select(kind => _chipByKind[(int)kind]), _chipByKind[(int)GameLogLineKind.Other]];

        PeriodOptions =
        [
            new GameLogPeriodOption(GameLogPeriod.Today, "Today"),
            new GameLogPeriodOption(GameLogPeriod.Last7Days, "Last 7 days"),
            new GameLogPeriodOption(GameLogPeriod.PickDate, "Pick date")
        ];
        _selectedPeriod = PeriodOptions[0];

        _RebuildCharacterOptions();
        _source.BufferReplaced += _OnBufferReplaced;
    }

    public BulkObservableCollection<GameLogRowViewModel> Rows { get; } = [];

    /// <summary>"All characters" first, then one entry per character by name.</summary>
    public ObservableCollection<GameLogCharacterOptionViewModel> CharacterOptions { get; } = [];

    public IReadOnlyList<GameLogKindChipViewModel> KindChips { get; }

    public IReadOnlyList<GameLogPeriodOption> PeriodOptions { get; }

    [ObservableProperty] private GameLogCharacterOptionViewModel? _selectedCharacter;

    [ObservableProperty] private GameLogPeriodOption _selectedPeriod;

    [ObservableProperty] private DateTime? _pickedDate;

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private bool _isTruncated;

    public bool IsPickDate => SelectedPeriod.Period == GameLogPeriod.PickDate;

    /// <summary>Reads the period from the watcher's buffer and starts listening for new lines. A second call reads
    /// again (a new period, a restarted watcher); a stale read is thrown away instead of applied.</summary>
    public async Task LoadAsync()
    {
        int version = ++_loadVersion;
        _isLoading = true;
        _Detach();
        _buffer = _source.CurrentBuffer;
        if (_buffer is null)
        {
            _isLoading = false;
            _loaded = [];
            _RebuildCharacterOptions();
            _ApplyFilter();
            StatusText = "The game log folder is not being watched yet.";
            return;
        }

        _buffer.LinesAdded += _OnLinesAdded;
        GameLogLineBuffer buffer = _buffer;
        (DateTime since, DateTime until) = _Range();
        try
        {
            (List<GameLogRowViewModel> read, bool truncated) = await Task.Run(() => _ReadRows(buffer, since, until));
            if (version != _loadVersion)
                return;

            _loaded = read;
            IsTruncated = truncated;
            _DropPendingAlreadyLoaded();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == _loadVersion)
                StatusText = $"The game logs could not be read: {exception.Message}";
            return;
        }
        finally
        {
            if (version == _loadVersion)
                _isLoading = false;
        }

        _RebuildCharacterOptions();
        _ApplyFilter();
        _FlushPending();
    }

    public void Dispose()
    {
        _source.BufferReplaced -= _OnBufferReplaced;
        _Detach();
    }

    partial void OnSelectedCharacterChanged(GameLogCharacterOptionViewModel? value)
    {
        if (!_isRebuildingOptions && value is not null)
            _ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => _ApplyFilter();

    partial void OnSelectedPeriodChanged(GameLogPeriodOption value)
    {
        OnPropertyChanged(nameof(IsPickDate));
        if (value.Period == GameLogPeriod.PickDate && PickedDate is null)
            PickedDate = _clock.GetUtcNow().UtcDateTime.Date; // setting it reads, through OnPickedDateChanged
        else
            _ = LoadAsync();
    }

    partial void OnPickedDateChanged(DateTime? value)
    {
        if (IsPickDate)
            _ = LoadAsync();
    }

    private void _OnBufferReplaced() => Dispatcher.UIThread.Post(() => _ = LoadAsync());

    private void _Detach()
    {
        if (_buffer is not null)
            _buffer.LinesAdded -= _OnLinesAdded;

        _buffer = null;
    }

    // Period bounds in EVE time: gamelog lines carry UTC, and "today" is EVE's day.
    private (DateTime Since, DateTime Until) _Range()
    {
        DateTime today = _clock.GetUtcNow().UtcDateTime.Date;
        return SelectedPeriod.Period switch
        {
            GameLogPeriod.Last7Days => (today.AddDays(-6), today.AddDays(1)),
            GameLogPeriod.PickDate => ((PickedDate ?? today).Date, (PickedDate ?? today).Date.AddDays(1)),
            _ => (today, today.AddDays(1))
        };
    }

    private (List<GameLogRowViewModel> Rows, bool Truncated) _ReadRows(GameLogLineBuffer buffer, DateTime since, DateTime until)
    {
        IReadOnlyList<GameLogLine> lines = buffer.Load(since, until);
        return ([.. lines.Skip(Math.Max(0, lines.Count - MaxLines)).Select(_RowOf)], lines.Count > MaxLines);
    }

    private GameLogRowViewModel _RowOf(GameLogLine line) => new(line, _faces.FaceOf(_IdOf(line.Character), line.Character));

    // A pilot whose log is on disk but who is not linked has no EVE id; a stable negative one keeps it one face.
    private long _IdOf(string character)
    {
        if (_characterIds.TryGetValue(character, out int id))
            return id;

        lock (_unlinkedIds)
        {
            if (!_unlinkedIds.TryGetValue(character, out int unlinked))
                _unlinkedIds[character] = unlinked = -(_unlinkedIds.Count + 1);
            return unlinked;
        }
    }

    // Lines that came in while the read ran may already be in what it returned — the same instances, not copies.
    private void _DropPendingAlreadyLoaded()
    {
        if (_pending.IsEmpty)
            return;

        HashSet<GameLogLine> loaded = new(_loaded.Select(row => row.Line), ReferenceEqualityComparer.Instance);
        List<GameLogLine> stillPending = [];
        while (_pending.TryDequeue(out GameLogLine? line))
        {
            if (!loaded.Contains(line))
                stillPending.Add(line);
        }

        foreach (GameLogLine line in stillPending)
            _pending.Enqueue(line);
    }

    private void _OnLinesAdded(IReadOnlyList<GameLogLine> lines)
    {
        foreach (GameLogLine line in lines)
            _pending.Enqueue(line);

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(_FlushPending, FlushInterval));
    }

    /// <summary>Adds what the watcher queued since the last flush. Public so a test does not wait for the timer.</summary>
    public void FlushPendingNow() => _FlushPending();

    private void _FlushPending()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        if (_isLoading)
            return;

        (DateTime since, DateTime until) = _Range();
        List<GameLogRowViewModel> added = [];
        while (_pending.TryDequeue(out GameLogLine? line))
        {
            if (line.Timestamp >= since && line.Timestamp < until)
                added.Add(_RowOf(line));
        }

        if (added.Count == 0)
            return;

        foreach (GameLogRowViewModel row in added)
            _Insert(_loaded, row);

        if (_loaded.Count > MaxLines)
        {
            _loaded.RemoveRange(0, _loaded.Count - MaxLines * 9 / 10);
            IsTruncated = true;
            _RebuildCharacterOptions();
            _ApplyFilter();
            return;
        }

        _AddToOptionCounts(added);
        bool[] enabled = _EnabledKinds();
        foreach (GameLogRowViewModel row in added)
        {
            if (!_MatchesCharacterAndSearch(row))
                continue;

            _chipByKind[(int)row.Kind].Count++;
            if (enabled[(int)row.Kind])
                _InsertVisible(row);
        }

        _UpdateStatus();
    }

    private void _Insert(List<GameLogRowViewModel> rows, GameLogRowViewModel row) =>
        rows.Insert(_IndexAfter(rows, row.Timestamp), row);

    private void _InsertVisible(GameLogRowViewModel row) => Rows.Insert(_IndexAfter(Rows, row.Timestamp), row);

    // Upper bound by time: a line keeps its place behind the lines of the same second that were there first.
    private static int _IndexAfter(IReadOnlyList<GameLogRowViewModel> rows, DateTime timestamp)
    {
        int low = 0, high = rows.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (rows[middle].Timestamp <= timestamp)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private bool[] _EnabledKinds()
    {
        bool[] enabled = new bool[Kinds.Length];
        foreach (GameLogKindChipViewModel chip in _chipByKind)
            enabled[(int)chip.Kind] = chip.IsOn;

        return enabled;
    }

    private bool _MatchesCharacterAndSearch(GameLogRowViewModel row)
    {
        if (SelectedCharacter is { IsAll: false, Name: { } name }
            && !string.Equals(row.CharacterName, name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string search = SearchText.Trim();
        return search.Length == 0
            || row.Line.Text.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.CharacterName.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void _ApplyFilter()
    {
        bool[] enabled = _EnabledKinds();
        int[] counts = new int[Kinds.Length];
        List<GameLogRowViewModel> shown = [];
        foreach (GameLogRowViewModel row in _loaded)
        {
            if (!_MatchesCharacterAndSearch(row))
                continue;

            counts[(int)row.Kind]++;
            if (enabled[(int)row.Kind])
                shown.Add(row);
        }

        foreach (GameLogKindChipViewModel chip in _chipByKind)
            chip.Count = counts[(int)chip.Kind];

        Rows.ReplaceAll(shown);
        _UpdateStatus();
    }

    private void _UpdateStatus() =>
        StatusText = _loaded.Count == 0
            ? "No game log lines in this period."
            : IsTruncated
                ? $"{Rows.Count:N0} of {_loaded.Count:N0} lines — the newest {MaxLines:N0} are kept"
                : $"{Rows.Count:N0} of {_loaded.Count:N0} lines";

    private void _AddToOptionCounts(IReadOnlyList<GameLogRowViewModel> added)
    {
        foreach (GameLogRowViewModel row in added)
        {
            foreach (GameLogCharacterOptionViewModel option in CharacterOptions)
            {
                if (option.IsAll || string.Equals(option.Name, row.CharacterName, StringComparison.OrdinalIgnoreCase))
                    option.Count++;
            }
        }
    }

    /// <summary>The linked characters plus any pilot whose log holds lines but who is not linked (local-only), by name,
    /// keeping the selection — a reload never silently jumps to someone else's lines.</summary>
    private void _RebuildCharacterOptions()
    {
        string? selectedName = SelectedCharacter?.Name;
        _isRebuildingOptions = true;
        try
        {
            CharacterOptions.Clear();
            GameLogCharacterOptionViewModel all = GameLogCharacterOptionViewModel.All();
            all.Count = _loaded.Count;
            CharacterOptions.Add(all);

            Dictionary<string, int> linesByCharacter = _loaded
                .GroupBy(row => row.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (string name in _characterIds.Keys.Concat(linesByCharacter.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                CharacterOptions.Add(new GameLogCharacterOptionViewModel(name, _faces.FaceOf(_IdOf(name), name))
                {
                    Count = linesByCharacter.GetValueOrDefault(name)
                });
            }

            SelectedCharacter = CharacterOptions.FirstOrDefault(option => option.Name is not null
                && string.Equals(option.Name, selectedName, StringComparison.OrdinalIgnoreCase)) ?? all;
        }
        finally
        {
            _isRebuildingOptions = false;
        }
    }
}
