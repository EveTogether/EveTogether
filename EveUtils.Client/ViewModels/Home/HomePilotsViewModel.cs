using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Esi;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>
/// PILOTS (ET-324): one compact row per own character, in place of the old cards.
///
/// <para><b>What moves each field, and how often.</b> Presence, portrait and ESI state are the shell's own live
/// <see cref="CharacterViewModel"/>. The system follows the game log, but a parsed line arrives many times a second in
/// a fight: the characters it names are only marked, and read on the next clock tick. The ship is the fit detection's
/// in-memory reading, looked at every 30 s — its own poll. The skill queue is read from storage every two minutes, the
/// importer's cadence. Only the clock moves time-left texts.</para>
///
/// <para><b>No SDE lookup on the UI thread</b> (ET-298): system security, hull and skill names are resolved inside
/// <c>Task.Run</c> once per id and cached for the session.</para>
/// </summary>
public sealed partial class HomePilotsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ShipEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueueEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TextEvery = TimeSpan.FromMinutes(1);

    private readonly ObservableCollection<CharacterViewModel> _characters;
    private readonly HomeNavigation _navigation;
    private readonly GamelogClientService? _gamelog;
    private readonly GamelogWatcherService? _watcher;
    private readonly IShipFitDetectionService? _ships;
    private readonly ICharacterSkillQueueRepository? _queues;
    private readonly ISdeAccessor? _sde;
    private readonly ITypeImageProvider? _typeImages;

    private readonly ConcurrentDictionary<string, double?> _security = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string> _typeNames = new();
    private readonly ConcurrentDictionary<string, byte> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SkillQueueStanding?> _standings = [];

    private DateTime _shipsShownAt = DateTime.MinValue;
    private DateTime _queuesReadAt = DateTime.MinValue;
    private DateTime _textsShownAt = DateTime.MinValue;
    private bool _isReadingQueues;
    private bool _isReconcilePosted;

    public HomePilotsViewModel(ObservableCollection<CharacterViewModel> characters, IServiceProvider? services,
        HomeNavigation navigation)
    {
        _characters = characters;
        _navigation = navigation;
        _gamelog = services?.GetService<GamelogClientService>();
        _watcher = services?.GetService<GamelogWatcherService>();
        _ships = services?.GetService<IShipFitDetectionService>();
        _queues = services?.GetService<ICharacterSkillQueueRepository>();
        _sde = services?.GetService<ISdeAccessor>();
        _typeImages = services?.GetService<ITypeImageProvider>();

        _characters.CollectionChanged += _OnCharactersChanged;
        if (_watcher is not null)
            _watcher.CharacterObserved += _OnCharacterObserved;
        _Reconcile();
    }

    public ObservableCollection<HomePilotRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _onThisPcText = string.Empty;
    [ObservableProperty] private string _whereTooltip = string.Empty;
    [ObservableProperty] private string _flyingTooltip = string.Empty;
    [ObservableProperty] private string _trainingTooltip = string.Empty;
    [ObservableProperty] private string _monthHeaderText = DateTime.Now.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>Raised after a queue read: the attention band lists the paused queues from it.</summary>
    public event Action? QueuesChanged;

    /// <summary>A character's ESI sign-in state moved: the attention band asks for a re-authorisation from it.</summary>
    public event Action? TokensChanged;

    /// <summary>Each own character's share today and this month, from the home's one runs read.</summary>
    public void ShowIsk(IReadOnlyDictionary<long, (decimal Today, decimal Month)> byCharacter, DateTime nowLocal)
    {
        MonthHeaderText = nowLocal.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        foreach (HomePilotRowViewModel row in Rows)
        {
            (decimal today, decimal month) = byCharacter.GetValueOrDefault(row.CharacterId);
            row.ShowIsk(today, month);
        }
    }

    /// <summary>The home's one-second clock. Reads nothing that is due later, and nothing at all on the UI thread.</summary>
    public void Tick(DateTime nowUtc)
    {
        _ShowObservedSystems();
        if (nowUtc - _shipsShownAt >= ShipEvery)
        {
            _shipsShownAt = nowUtc;
            _ShowShips();
        }

        if (nowUtc - _queuesReadAt >= QueueEvery)
            _ = ReadQueuesAsync();

        if (nowUtc - _textsShownAt >= TextEvery)
        {
            _textsShownAt = nowUtc;
            foreach (HomePilotRowViewModel row in Rows)
                row.Tick(nowUtc);
        }
    }

    /// <summary>Every character's queue, read and named off the UI thread; one read at a time.</summary>
    public async Task ReadQueuesAsync()
    {
        if (_queues is not { } queues || _isReadingQueues)
            return;

        _isReadingQueues = true;
        _queuesReadAt = DateTime.UtcNow;
        try
        {
            int[] ids = [.. Rows.Where(row => row.SharesQueue).Select(row => row.CharacterId)];
            Dictionary<int, SkillQueueStanding?> read = await Task.Run(async () =>
            {
                Dictionary<int, SkillQueueStanding?> standings = [];
                foreach (int id in ids)
                {
                    IReadOnlyList<CharacterSkillQueueEntry> entries = await queues.GetForCharacterAsync(id);
                    standings[id] = SkillQueueStanding.From(entries, _TypeNameOffThread);
                }

                return standings;
            });

            foreach ((int id, SkillQueueStanding? standing) in read)
                _standings[id] = standing;
            foreach (HomePilotRowViewModel row in Rows)
                if (_standings.TryGetValue(row.CharacterId, out SkillQueueStanding? standing))
                    row.ShowQueue(standing);
            QueuesChanged?.Invoke();
        }
        finally
        {
            _isReadingQueues = false;
        }
    }

    /// <summary>The shell rebuilds its list with a Clear and one Add per character, all in one go: reconciling once
    /// after the last of them keeps every row — and what it shows — instead of dropping all and building them anew.</summary>
    private void _OnCharactersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isReconcilePosted)
            return;

        _isReconcilePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isReconcilePosted = false;
            _Reconcile();
        });
    }

    /// <summary>A row per character id; a rebuilt shell row is only swapped in under it, so nothing is read again.</summary>
    private void _Reconcile()
    {
        Dictionary<int, HomePilotRowViewModel> shown = Rows.ToDictionary(row => row.CharacterId);
        List<HomePilotRowViewModel> target = [];
        foreach (CharacterViewModel character in _characters.Where(character => character.CharacterId > 0))
        {
            if (shown.TryGetValue(character.CharacterId, out HomePilotRowViewModel? row))
            {
                if (!ReferenceEquals(row.Character, character))
                {
                    row.Character.PropertyChanged -= _OnCharacterPropertyChanged;
                    row.Character = character;
                    character.PropertyChanged += _OnCharacterPropertyChanged;
                }
            }
            else
            {
                row = new HomePilotRowViewModel(character, _navigation);
                character.PropertyChanged += _OnCharacterPropertyChanged;
                if (_standings.TryGetValue(character.CharacterId, out SkillQueueStanding? standing))
                    row.ShowQueue(standing);
                _observed.TryAdd(character.Name, 0);
            }

            target.Add(row);
        }

        foreach (HomePilotRowViewModel gone in Rows.Where(row => !target.Contains(row)))
            gone.Character.PropertyChanged -= _OnCharacterPropertyChanged;
        Rows.ReconcileTo(target);
        _ShowHeader();
    }

    private void _OnCharacterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CharacterViewModel.EsiTokenStatus))
            TokensChanged?.Invoke();
        if (e.PropertyName != nameof(CharacterViewModel.HasActiveClient) || sender is not CharacterViewModel character)
            return;

        if (Rows.FirstOrDefault(row => ReferenceEquals(row.Character, character)) is not { } changed)
            return;

        changed.ShowPresence();
        _observed.TryAdd(character.Name, 0);
        _shipsShownAt = DateTime.MinValue;
        _ShowHeader();
    }

    private void _OnCharacterObserved(string name) => _observed.TryAdd(name, 0);

    private void _ShowObservedSystems()
    {
        if (_gamelog is null || _observed.IsEmpty)
            return;

        foreach (string name in _observed.Keys)
        {
            _observed.TryRemove(name, out _);
            if (Rows.FirstOrDefault(row => string.Equals(row.Character.Name, name, StringComparison.OrdinalIgnoreCase)) is not { IsOnThisPc: true } row)
                continue;

            string? system = _gamelog.Snapshot(name).Location;
            if (string.IsNullOrWhiteSpace(system))
            {
                row.ShowSystem(null, null);
                continue;
            }

            if (_security.TryGetValue(system, out double? security))
                row.ShowSystem(system, security);
            else
                _ = _ResolveSecurityAsync(row, system);
        }
    }

    private async Task _ResolveSecurityAsync(HomePilotRowViewModel row, string system)
    {
        row.ShowSystem(system, null);
        double? security = await Task.Run(() => _sde?.FindSolarSystemByName(system)?.SecurityStatus);
        _security[system] = security;
        if (row.SystemName == system)
            row.ShowSystem(system, security);
    }

    private void _ShowShips()
    {
        if (_ships is null)
            return;

        foreach (HomePilotRowViewModel row in Rows.Where(row => row.IsOnThisPc && row.SharesShip))
        {
            ShipFitDetectionReading reading = _ships.GetReading(row.CharacterId);
            if (reading.ShipTypeId is not { } hull)
            {
                row.ShowShip(reading, string.Empty);
                continue;
            }

            if (_typeNames.TryGetValue(hull, out string? name))
                row.ShowShip(reading, name);
            else
                _ = _ResolveHullAsync(row, reading, hull);
        }
    }

    private async Task _ResolveHullAsync(HomePilotRowViewModel row, ShipFitDetectionReading reading, int hull)
    {
        string name = await Task.Run(() => _TypeNameOffThread(hull));
        row.ShowShip(reading, name);
        if (_typeImages is not null)
            row.HullIcon = await _typeImages.GetImageAsync(hull, TypeImageKind.Icon, 32);
    }

    /// <summary>Only ever called inside <c>Task.Run</c>: one SDE query per id for the session, then the cache.</summary>
    private string _TypeNameOffThread(int typeId) =>
        _typeNames.GetOrAdd(typeId, id => _sde is not null && _sde.TryGetTypeName(id, out string name) ? name : $"#{id}");

    private void _ShowHeader()
    {
        int total = Rows.Count;
        OnThisPcText = $"{Rows.Count(row => row.IsOnThisPc)} of {total} on this PC";
        WhereTooltip = $"From the game log on this PC. Location shared by {Rows.Count(row => row.SharesLocation)} of {total}.";
        FlyingTooltip = $"Ship and detected fit. Current ship shared by {Rows.Count(row => row.SharesShip)} of {total}.";
        TrainingTooltip = $"Skill in training and how long the queue lasts. Skill queue shared by {Rows.Count(row => row.SharesQueue)} of {total}.";
    }

    public void Dispose()
    {
        _characters.CollectionChanged -= _OnCharactersChanged;
        if (_watcher is not null)
            _watcher.CharacterObserved -= _OnCharacterObserved;
        foreach (HomePilotRowViewModel row in Rows)
            row.Character.PropertyChanged -= _OnCharacterPropertyChanged;
    }
}
