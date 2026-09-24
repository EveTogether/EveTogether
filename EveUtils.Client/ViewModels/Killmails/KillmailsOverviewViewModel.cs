using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Client.Killmails;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Queries;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>
/// ET-332: the KILLMAILS overview, one character at a time — a character tile row (GRANT ACCESS instead of a count
/// while <c>esi-killmails.read_killmails.v1</c> is missing), a SHOW row (All/Kills/Losses/Not linked, single-select),
/// a search box, totals and the mails themselves grouped under a local-day header, newest first. A hosted module like
/// RUNS (<see cref="IDialogService.ShowKillmails"/>): it can dock as a tab or float as its own window.
///
/// <para>Every read is scoped to <see cref="SelectedCharacter"/> alone — switching characters is a fresh
/// <see cref="GetKillmailsOverviewQuery"/>, not a client-side filter over everyone's mails, the same per-character
/// split the killmail tables themselves keep. The SHOW filter and the search box never read again: both recompute
/// <see cref="Days"/> from the last read's rows, kept in <c>_allRows</c>.</para>
///
/// <para><b>Nothing is read on the UI thread</b> (the same rule <c>RunsOverviewViewModel</c> states for itself): the
/// query, the SDE lookups and <see cref="KillmailNames.HydrateAsync"/> all happen inside one <c>Task.Run</c>. A stale
/// read is discarded rather than applied — <see cref="_readVersion"/> is bumped at the start of every
/// <see cref="_ReadAsync"/> and checked before the result is used, so switching characters twice quickly can never
/// leave the second character's tile showing the first character's mails.</para>
/// </summary>
public sealed partial class KillmailsOverviewViewModel : ViewModelBase, IRefreshableModule
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IServiceProvider _services;
    private readonly Func<int, string, Task> _allowScope;
    private readonly ISdeAccessor _sde;
    private readonly RunRowFacts _facts;
    private readonly KillmailNames _names;
    private readonly CharacterFaceCache _faces;
    private readonly TimeProvider _clock;
    private readonly KillmailShowFilterTileViewModel _allFilter;
    private readonly KillmailShowFilterTileViewModel _killsFilter;
    private readonly KillmailShowFilterTileViewModel _lossesFilter;
    private readonly KillmailShowFilterTileViewModel _notLinkedFilter;

    private IReadOnlyList<KillmailRowViewModel> _allRows = [];

    /// <summary>Bumped at the start of every <see cref="_ReadAsync"/>; a read whose stamp no longer matches when it
    /// finishes was superseded by a later character switch and its result is thrown away instead of applied.</summary>
    private int _readVersion;

    public KillmailsOverviewViewModel(CqrsDispatcher dispatcher, IServiceProvider services,
        IReadOnlyList<Character> characters, Func<int, string, Task> allowScope)
    {
        _dispatcher = dispatcher;
        _services = services;
        _allowScope = allowScope;
        _sde = services.GetRequiredService<ISdeAccessor>();
        _facts = new RunRowFacts(_sde);
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _faces = new CharacterFaceCache(services.GetService<ICharacterPortraitProvider>());

        Dictionary<int, string> ownNames = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => character.EsiCharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name);
        _names = new KillmailNames(ownNames, services.GetService<IExternalCharacterLookup>(),
            services.GetRequiredService<IEsiAffiliationResolver>(), _sde,
            services.GetRequiredService<IKillmailEntityNameRepository>(), services.GetRequiredService<ISettingRepository>(),
            _clock);

        _allFilter = new KillmailShowFilterTileViewModel(KillmailShowFilter.All, "All", _SelectFilter) { IsOn = true };
        _killsFilter = new KillmailShowFilterTileViewModel(KillmailShowFilter.Kills, "Kills", _SelectFilter);
        _lossesFilter = new KillmailShowFilterTileViewModel(KillmailShowFilter.Losses, "Losses", _SelectFilter);
        _notLinkedFilter = new KillmailShowFilterTileViewModel(KillmailShowFilter.NotLinked, "Not linked to a run", _SelectFilter);
        Filters = [_allFilter, _killsFilter, _lossesFilter, _notLinkedFilter];

        _RebuildCharacterTiles(characters);
        _ShowEmpty();
    }

    public ObservableCollection<KillmailCharacterOptionViewModel> Characters { get; } = [];

    public IReadOnlyList<KillmailShowFilterTileViewModel> Filters { get; }

    public ObservableCollection<KillmailDayViewModel> Days { get; } = [];

    /// <summary>Bindable so the empty-state GRANT ACCESS button can reach this character's own command — nothing in
    /// the view binds it TwoWay, selection always flows through a tile's <c>SelectCommand</c>.</summary>
    [ObservableProperty] private KillmailCharacterOptionViewModel? _selectedCharacter;

    private KillmailShowFilter _selectedFilter = KillmailShowFilter.All;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _statusMessage;

    /// <summary><see cref="SelectedCharacter"/> is missing the killmails scope: the view shows GRANT ACCESS instead
    /// of the list (ET-332 AC3), never an empty list that reads as "no kills".</summary>
    [ObservableProperty] private bool _needsAccess;

    [ObservableProperty] private int _killsCount;

    [ObservableProperty] private int _lossesCount;

    [ObservableProperty] private string _iskDestroyedText = IskFormat.Compact(0m);

    [ObservableProperty] private string _iskLostText = IskFormat.Compact(0m);

    /// <summary>ISK destroyed over destroyed plus lost, one decimal — "—" for an empty character or one with nothing
    /// priced, never 0% or a division by zero (ET-332 AC2).</summary>
    [ObservableProperty] private string _efficiencyText = "—";

    public Task LoadAsync(CancellationToken cancellationToken = default) => _ReadAsync(cancellationToken);

    /// <summary>Re-opening an already-open KILLMAILS tab does not construct a new view model — <c>ModuleHostService</c>
    /// hands the re-select straight to this instance instead (ET-46 pattern). Re-reads the character registry so a
    /// scope granted since this screen was built (GRANT ACCESS's whole point) and a mail <c>KillmailRefreshService</c>
    /// picked up in the background both show without the pilot having to close the tab first.</summary>
    public void RefreshModule() => _ = _RefreshCharactersAndReadAsync();

    [RelayCommand]
    private Task RefreshAsync() => _ReadAsync();

    private async Task _RefreshCharactersAndReadAsync()
    {
        if (_services.GetService<ICharacterRegistry>() is { } registry)
        {
            _RebuildCharacterTiles(await registry.GetAllAsync());
        }

        await _ReadAsync();
    }

    /// <summary>Rebuilds every character tile from <paramref name="characters"/>, keeping the same character selected
    /// by id when it is still there (falling back to the first tile), so a REFRESH never silently jumps the pilot to
    /// someone else's kills.</summary>
    private void _RebuildCharacterTiles(IReadOnlyList<Character> characters)
    {
        int? selectedCharacterId = SelectedCharacter?.CharacterId;
        Characters.Clear();
        foreach (Character character in characters.Where(character => character.EsiCharacterId is > 0)
                     .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase))
        {
            int characterId = character.EsiCharacterId!.Value;
            Characters.Add(new KillmailCharacterOptionViewModel(characterId, character.Name,
                _faces.FaceOf(characterId, character.Name), !character.HasScope(KillmailsScopeCatalog.ReadKillmails),
                _SelectCharacter, id => _allowScope(id, KillmailsScopeCatalog.ReadKillmails)));
        }

        KillmailCharacterOptionViewModel? selected = selectedCharacterId is { } id
            ? Characters.FirstOrDefault(tile => tile.CharacterId == id)
            : null;
        selected ??= Characters.FirstOrDefault();
        SelectedCharacter = selected;
        foreach (KillmailCharacterOptionViewModel tile in Characters)
        {
            tile.IsSelected = ReferenceEquals(tile, selected);
        }
    }

    private void _SelectCharacter(KillmailCharacterOptionViewModel character)
    {
        if (ReferenceEquals(SelectedCharacter, character))
        {
            return;
        }

        SelectedCharacter = character;
        foreach (KillmailCharacterOptionViewModel tile in Characters)
        {
            tile.IsSelected = ReferenceEquals(tile, character);
        }

        _ = _ReadAsync();
    }

    private void _SelectFilter(KillmailShowFilter filter)
    {
        if (_selectedFilter == filter)
        {
            return;
        }

        _selectedFilter = filter;
        foreach (KillmailShowFilterTileViewModel tile in Filters)
        {
            tile.IsOn = tile.Key == filter;
        }

        _ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => _ApplyFilter();

    private async Task _ReadAsync(CancellationToken cancellationToken = default)
    {
        int version = ++_readVersion;
        KillmailCharacterOptionViewModel? character = SelectedCharacter;
        if (character is null || character.NeedsAccess)
        {
            NeedsAccess = character is not null;
            _allRows = [];
            if (character is not null)
            {
                character.Count = 0;
            }

            _ShowEmpty();
            return;
        }

        IsBusy = true;
        NeedsAccess = false;
        StatusMessage = null;
        try
        {
            int characterId = character.CharacterId;
            // The dispatcher call, the SDE lookups and KillmailNames.HydrateAsync are all synchronous-under-the-hood
            // SQLite work (plus HydrateAsync's own ESI calls) — one Task.Run for the whole read, the same reason
            // RunsOverviewViewModel keeps its own reads off the UI thread entirely.
            (Result<IReadOnlyList<KillmailOverviewRowDto>> result, IReadOnlyList<KillmailRowViewModel> rows) =
                await Task.Run(() => _ReadAndBuildAsync(characterId, cancellationToken), cancellationToken);

            if (version != _readVersion)
            {
                return; // superseded by a later character switch — this result is stale, never applied
            }

            if (!result.IsSuccess)
            {
                StatusMessage = result.Messages.Count > 0 ? result.Messages[0].Text : "The killmails could not be read.";
                _allRows = [];
                character.Count = 0;
                _ShowEmpty();
                return;
            }

            _allRows = rows;
            character.Count = _allRows.Count;
            _RefreshTotals();
            _ApplyFilter();
        }
        finally
        {
            if (version == _readVersion)
            {
                IsBusy = false;
            }
        }
    }

    private async Task<(Result<IReadOnlyList<KillmailOverviewRowDto>> Result, IReadOnlyList<KillmailRowViewModel> Rows)> _ReadAndBuildAsync(
        int characterId, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KillmailOverviewRowDto>> result =
            await _dispatcher.Query(new GetKillmailsOverviewQuery(characterId), cancellationToken);
        if (!result.IsSuccess)
        {
            return (result, []);
        }

        IReadOnlyList<KillmailOverviewRowDto> dtos = result.Value ?? [];
        // Only the ids a row can actually show: a corporation or alliance is only ever read when there is no
        // character id to name instead (see _VictimName/_FinalBlowName), so a mail's own corp is never asked for
        // twice over and an id nothing on screen shows never reaches ESI at all.
        await _names.HydrateAsync(
            [.. dtos.SelectMany(dto => new[] { dto.VictimCharacterId, dto.FinalBlow?.CharacterId }).OfType<int>()],
            [.. dtos.Where(dto => dto.VictimCharacterId is null).Select(dto => dto.VictimCorporationId)
                .Concat(dtos.Where(dto => dto.FinalBlow?.CharacterId is null).Select(dto => dto.FinalBlow?.CorporationId))
                .OfType<int>()],
            [.. dtos.Where(dto => dto.VictimCharacterId is null && dto.VictimCorporationId is null)
                .Select(dto => dto.VictimAllianceId).OfType<int>()],
            cancellationToken);

        return (result, [.. dtos.Select(_BuildRow)]);
    }

    private KillmailRowViewModel _BuildRow(KillmailOverviewRowDto dto)
    {
        SdeSolarSystem? system = _facts.SystemOf(dto.SolarSystemId);
        string systemName = system?.Name ?? $"system {dto.SolarSystemId}";
        string securityText = system is null ? "—" : RunRowFacts.SecurityText(system.SecurityStatus);
        bool isAbyssal = AbyssalSpace.IsAbyssalSystem(dto.SolarSystemId);
        string shipName = _sde.GetType(dto.VictimShipTypeId)?.Name ?? $"type {dto.VictimShipTypeId}";
        string counterparty = dto.IsLoss ? _FinalBlowName(dto.FinalBlow) : _VictimName(dto);
        return new KillmailRowViewModel(dto, shipName, systemName, system?.RegionName, isAbyssal, securityText,
            counterparty, _clock.LocalTimeZone, _OpenDetailAsync);
    }

    private string _VictimName(KillmailOverviewRowDto dto) =>
        dto.VictimCharacterId is { } characterId ? _names.NameOf(characterId)
        : dto.VictimCorporationId is { } corporationId ? _names.NameOf(corporationId)
        : dto.VictimAllianceId is { } allianceId ? _names.NameOf(allianceId)
        : "NPC";

    private string _FinalBlowName(KillmailFinalBlowDto? finalBlow) => finalBlow switch
    {
        { CharacterId: { } characterId } => _names.NameOf(characterId),
        { CorporationId: { } corporationId } => _names.NameOf(corporationId),
        { FactionId: { } factionId } => _names.NameOf(factionId),
        _ => "NPC"
    };

    // ET-333 hands in the real delegate that opens the killmail detail dialog; until then the row's OpenCommand is a
    // no-op, the same seam ActivityOverviewRowViewModel._openDetail gives RUNS before its own detail screen existed.
    private static Task _OpenDetailAsync(KillmailRowViewModel row) => Task.CompletedTask;

    private void _RefreshTotals()
    {
        decimal destroyed = _allRows.Where(row => !row.IsLoss).Sum(row => row.Isk ?? 0m);
        decimal lost = -_allRows.Where(row => row.IsLoss).Sum(row => row.Isk ?? 0m);
        KillsCount = _allRows.Count(row => !row.IsLoss);
        LossesCount = _allRows.Count(row => row.IsLoss);
        IskDestroyedText = IskFormat.Compact(destroyed);
        IskLostText = IskFormat.Compact(lost);
        decimal total = destroyed + lost;
        EfficiencyText = total <= 0 ? "—" : (destroyed / total * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private void _ShowEmpty()
    {
        KillsCount = 0;
        LossesCount = 0;
        IskDestroyedText = IskFormat.Compact(0m);
        IskLostText = IskFormat.Compact(0m);
        EfficiencyText = "—";
        foreach (KillmailShowFilterTileViewModel tile in Filters)
        {
            tile.Count = 0;
        }

        Days.Clear();
    }

    /// <summary>Recomputes <see cref="Days"/> and the SHOW row's counts from <c>_allRows</c> — no read of its own, so
    /// switching SHOW or typing a search term is as cheap as folding a RUNS day.</summary>
    private void _ApplyFilter()
    {
        _allFilter.Count = _allRows.Count;
        _killsFilter.Count = _allRows.Count(row => !row.IsLoss);
        _lossesFilter.Count = _allRows.Count(row => row.IsLoss);
        _notLinkedFilter.Count = _allRows.Count(row => row.IsLoss && row.RunId is null);

        IEnumerable<KillmailRowViewModel> scoped = _selectedFilter switch
        {
            KillmailShowFilter.Kills => _allRows.Where(row => !row.IsLoss),
            KillmailShowFilter.Losses => _allRows.Where(row => row.IsLoss),
            KillmailShowFilter.NotLinked => _allRows.Where(row => row.IsLoss && row.RunId is null),
            _ => _allRows
        };

        string needle = SearchText.Trim();
        if (needle.Length > 0)
        {
            scoped = scoped.Where(row => row.Matches(needle));
        }

        Days.Clear();
        foreach (IGrouping<DateOnly, KillmailRowViewModel> group in scoped.GroupBy(row => row.Day).OrderByDescending(group => group.Key))
        {
            Days.Add(new KillmailDayViewModel(group.Key, [.. group.OrderByDescending(row => row.KillmailTimeUtc)]));
        }
    }
}
