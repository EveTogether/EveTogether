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
/// </summary>
public sealed partial class KillmailsOverviewViewModel : ViewModelBase
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly ISdeAccessor _sde;
    private readonly RunRowFacts _facts;
    private readonly KillmailNames _names;
    private readonly KillmailShowFilterTileViewModel _allFilter;
    private readonly KillmailShowFilterTileViewModel _killsFilter;
    private readonly KillmailShowFilterTileViewModel _lossesFilter;
    private readonly KillmailShowFilterTileViewModel _notLinkedFilter;

    private IReadOnlyList<KillmailRowViewModel> _allRows = [];

    public KillmailsOverviewViewModel(CqrsDispatcher dispatcher, IServiceProvider services,
        IReadOnlyList<Character> characters, Func<int, string, Task> allowScope)
    {
        _dispatcher = dispatcher;
        _sde = services.GetRequiredService<ISdeAccessor>();
        _facts = new RunRowFacts(_sde);

        Dictionary<int, string> ownNames = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => character.EsiCharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name);
        _names = new KillmailNames(ownNames, services.GetService<IExternalCharacterLookup>(),
            services.GetRequiredService<IEsiAffiliationResolver>(), _sde,
            services.GetRequiredService<IKillmailEntityNameRepository>(), services.GetRequiredService<ISettingRepository>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System);

        _allFilter = new KillmailShowFilterTileViewModel(KillmailShowFilter.All, "All", _SelectFilter) { IsOn = true };
        _killsFilter = new KillmailShowFilterTileViewModel(KillmailShowFilter.Kills, "Kills", _SelectFilter);
        _lossesFilter = new KillmailShowFilterTileViewModel(KillmailShowFilter.Losses, "Losses", _SelectFilter);
        _notLinkedFilter = new KillmailShowFilterTileViewModel(KillmailShowFilter.NotLinked, "Not linked to a run", _SelectFilter);
        Filters = [_allFilter, _killsFilter, _lossesFilter, _notLinkedFilter];

        CharacterFaceCache faces = new(services.GetService<ICharacterPortraitProvider>());
        foreach (Character character in characters.Where(character => character.EsiCharacterId is > 0)
                     .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase))
        {
            int characterId = character.EsiCharacterId!.Value;
            Characters.Add(new KillmailCharacterOptionViewModel(characterId, character.Name,
                faces.FaceOf(characterId, character.Name), !character.HasScope(KillmailsScopeCatalog.ReadKillmails),
                _SelectCharacter, id => allowScope(id, KillmailsScopeCatalog.ReadKillmails)));
        }

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

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        KillmailCharacterOptionViewModel? initial = Characters.FirstOrDefault();
        if (initial is not null)
        {
            SelectedCharacter = initial;
            initial.IsSelected = true;
        }

        await _ReadAsync(cancellationToken);
    }

    [RelayCommand]
    private Task RefreshAsync() => _ReadAsync();

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
            // The dispatcher call and the SDE lookups below are synchronous under the hood (SQLite), the same reason
            // RunsOverviewViewModel keeps its own reads off the UI thread — an await alone would not have moved them.
            int characterId = character.CharacterId;
            Result<IReadOnlyList<KillmailOverviewRowDto>> result = await Task.Run(
                () => _dispatcher.Query(new GetKillmailsOverviewQuery(characterId), cancellationToken), cancellationToken);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Messages.Count > 0 ? result.Messages[0].Text : "The killmails could not be read.";
                _allRows = [];
                character.Count = 0;
                _ShowEmpty();
                return;
            }

            IReadOnlyList<KillmailOverviewRowDto> dtos = result.Value ?? [];
            await _names.HydrateAsync(
                [.. dtos.SelectMany(dto => new[] { dto.VictimCharacterId, dto.FinalBlow?.CharacterId }).OfType<int>()],
                [.. dtos.SelectMany(dto => new[] { dto.VictimCorporationId, dto.FinalBlow?.CorporationId }).OfType<int>()],
                [.. dtos.Select(dto => dto.VictimAllianceId).OfType<int>()],
                cancellationToken);

            _allRows = await Task.Run(() => (IReadOnlyList<KillmailRowViewModel>)[.. dtos.Select(_BuildRow)], cancellationToken);
            character.Count = _allRows.Count;
            _RefreshTotals();
            _ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
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
            counterparty, _OpenDetailAsync);
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
