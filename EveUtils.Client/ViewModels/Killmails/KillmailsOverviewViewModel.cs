using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Client.Killmails;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Enums;
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
///
/// <para><b>Live scope grants (ET-363):</b> a GRANT ACCESS granted while this screen stands open comes through
/// <see cref="ICharacterRegistry.RegistryChanged"/> — the same seam <c>SkillRefreshService</c>, <c>ImplantRefreshService</c>,
/// <c>ShipFitDetectionService</c> and <c>MetricsWindowViewModel</c> already use for a changed character/scope status —
/// rather than waiting for a re-open (<see cref="RefreshModule"/>) that may never come. A new killmail the background
/// <c>KillmailRefreshService</c> finds on its own 5-minute tick reaches this screen through
/// <see cref="KillmailsChangeFeed"/> (ET-363 AC3, ET-383), the same feed a run link made in another window arrives on.</para>
/// </summary>
public sealed partial class KillmailsOverviewViewModel : ViewModelBase, IRefreshableModule, IDisposable
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly Func<int, string, Task> _allowScope;
    private readonly ISdeAccessor _sde;
    private readonly RunRowFacts _facts;
    private readonly KillmailNames _names;
    private readonly CharacterFaceCache _faces;
    private readonly TimeProvider _clock;
    private readonly ICharacterRegistry? _registry;
    private readonly IDisposable? _changeSubscription;
    private readonly KillmailShowFilterTileViewModel _allFilter;
    private readonly KillmailShowFilterTileViewModel _killsFilter;
    private readonly KillmailShowFilterTileViewModel _lossesFilter;
    private readonly KillmailShowFilterTileViewModel _notLinkedFilter;

    private IReadOnlyList<KillmailRowViewModel> _allRows = [];

    /// <summary>ET-340: rows parsed from pasted clipboard text, not yet confirmed by a real ESI mail — merged into
    /// <see cref="Days"/> alongside <c>_allRows</c>, but never into it: they carry no ISK and no run link, so keeping
    /// them out of <c>_allRows</c> keeps every existing total, count and SHOW filter reading only confirmed mails.</summary>
    private IReadOnlyList<KillmailRowViewModel> _provisionalRows = [];

    /// <summary>Bumped at the start of every <see cref="_ReadAsync"/>; a read whose stamp no longer matches when it
    /// finishes was superseded by a later character switch and its result is thrown away instead of applied.</summary>
    private int _readVersion;

    public KillmailsOverviewViewModel(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services,
        IReadOnlyList<Character> characters, Func<int, string, Task> allowScope)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _services = services;
        _allowScope = allowScope;
        _sde = services.GetRequiredService<ISdeAccessor>();
        _facts = new RunRowFacts(_sde);
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _faces = new CharacterFaceCache(services.GetService<ICharacterPortraitProvider>());

        Dictionary<int, string> ownNames = [];
        foreach (Character character in characters)
        {
            if (character.EsiCharacterId is { } id && id > 0 && !ownNames.ContainsKey(id))
            {
                ownNames[id] = character.Name;
            }
        }

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

        _registry = services.GetService<ICharacterRegistry>();
        if (_registry is not null)
        {
            _registry.RegistryChanged += _OnRegistryChanged;
        }

        _changeSubscription = services.GetService<KillmailsChangeFeed>()?.Subscribe(changes =>
            changes.Any(change => change.Data.Kind == KillmailsChangeKind.Imported) ? _RefreshCharactersAndReadAsync() : _ReadAsync());
    }

    /// <summary>Releases the <see cref="ICharacterRegistry.RegistryChanged"/> and <see cref="KillmailsChangeFeed"/>
    /// subscriptions — called by <c>KillmailsWindow</c>'s own <c>Closed</c> handler, the same pattern
    /// <c>MetricsWindow</c> uses.</summary>
    public void Dispose()
    {
        if (_registry is not null)
        {
            _registry.RegistryChanged -= _OnRegistryChanged;
        }

        _changeSubscription?.Dispose();
    }

    // A scope granted or lost while this screen stands open (GRANT ACCESS's whole point, ET-363) — may fire from a
    // background thread (a re-auth's own registry write, or another module's background refresh), so the rebuild
    // that touches the bound Characters/Days collections has to be posted to the UI thread rather than run inline.
    private void _OnRegistryChanged() => Dispatcher.UIThread.Post(() => _ = _RefreshCharactersAndReadAsync());

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

    /// <summary>PASTE LINK (ET-338 + ET-340): reads an ESI killmail link, an in-game <c>killReport:</c> link, or the
    /// killmail's own "Copy" clipboard text off the clipboard. A link wins when both are present, imported directly
    /// and bypassing the 5-minute cache on the character feed; the text falls back to a provisional row, replaced
    /// once the real mail lands. The clipboard watch (<see cref="EveUtils.Client.Clipboard.ClipboardWatchService"/>)
    /// is opt-in, so this button is the route that works for a pilot who never turned it on.</summary>
    [RelayCommand]
    private async Task PasteLinkAsync()
    {
        IDialogService? dialogs = _services.GetService<IDialogService>();
        string? text = dialogs is null ? null : await dialogs.GetClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            _services.GetService<IToastService>()?.Show("No killmail on the clipboard",
                "Copy an ESI killmail link, an in-game killmail chat link, or the killmail's own \"Copy\" text, then paste again.",
                ToastKind.Error);
            return;
        }

        if (KillmailLink.TryParse(text, out int killmailId, out string hash))
        {
            await _PasteLinkAsync(killmailId, hash);
            return;
        }

        await _PasteTextAsync(text);
    }

    private async Task _PasteLinkAsync(int killmailId, string hash)
    {
        IsBusy = true;
        KillmailImportResult result;
        try
        {
            result = await _services.GetRequiredService<EsiKillmailImporter>().ImportOneAsync(killmailId, hash);
        }
        finally
        {
            IsBusy = false;
        }

        if (result.Status != KillmailImportStatus.Imported)
        {
            _services.GetService<IToastService>()?.Show($"Killmail {killmailId} not imported", result.Message, ToastKind.Error);
            return;
        }

        _services.GetService<IToastService>()?.Show($"Killmail {killmailId} imported", null, ToastKind.Success);
        await _RefreshCharactersAndReadAsync();
    }

    private async Task _PasteTextAsync(string text)
    {
        IsBusy = true;
        ProvisionalKillmailImportResult result;
        try
        {
            result = await _services.GetRequiredService<ProvisionalKillmailImporter>().ImportAsync(text);
        }
        finally
        {
            IsBusy = false;
        }

        if (result.Status != ProvisionalKillmailImportStatus.Imported)
        {
            _services.GetService<IToastService>()?.Show("Killmail not imported", result.Message, ToastKind.Error);
            return;
        }

        _services.GetService<IToastService>()?.Show("Killmail added", "Provisional — waiting for the real mail.", ToastKind.Success);
        await _RefreshCharactersAndReadAsync();
    }

    private async Task _RefreshCharactersAndReadAsync()
    {
        // RefreshModule is fire-and-forget by IRefreshableModule's own contract, so a failure here has to end up in
        // StatusMessage rather than an unobserved exception — the read below catches its own, but the registry call
        // happens before that try starts.
        try
        {
            if (_services.GetService<ICharacterRegistry>() is { } registry)
            {
                _RebuildCharacterTiles(await registry.GetAllAsync());
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusMessage = $"The character list could not be refreshed: {exception.Message}";
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
        foreach (Character character in characters.OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (character.EsiCharacterId is not { } characterId || characterId <= 0)
            {
                continue;
            }

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
            _provisionalRows = [];
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
            (Result<IReadOnlyList<KillmailOverviewRowDto>> result, IReadOnlyList<KillmailRowViewModel> rows,
                IReadOnlyList<KillmailRowViewModel> provisionalRows) =
                await Task.Run(() => _ReadAndBuildAsync(characterId, cancellationToken), cancellationToken);

            if (version != _readVersion)
            {
                return; // superseded by a later character switch — this result is stale, never applied
            }

            if (!result.IsSuccess)
            {
                StatusMessage = result.Messages.Count > 0 ? result.Messages[0].Text : "The killmails could not be read.";
                _allRows = [];
                _provisionalRows = [];
                character.Count = 0;
                _ShowEmpty();
                return;
            }

            _allRows = rows;
            _provisionalRows = provisionalRows;
            character.Count = _allRows.Count;
            _RefreshTotals();
            _ApplyFilter();
        }
        // Both RefreshModule and a tile's SelectCommand fire this off without awaiting it (RefreshModule is void by
        // IRefreshableModule's own contract), so an exception with nobody to catch it would otherwise go unobserved —
        // this is the module's one chance to say so instead of silently doing nothing.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == _readVersion)
            {
                StatusMessage = $"The killmails could not be read: {exception.Message}";
                _allRows = [];
                _provisionalRows = [];
                character.Count = 0;
                _ShowEmpty();
            }
        }
        finally
        {
            if (version == _readVersion)
            {
                IsBusy = false;
            }
        }
    }

    private async Task<(Result<IReadOnlyList<KillmailOverviewRowDto>> Result, IReadOnlyList<KillmailRowViewModel> Rows,
        IReadOnlyList<KillmailRowViewModel> ProvisionalRows)> _ReadAndBuildAsync(int characterId, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KillmailOverviewRowDto>> result =
            await _dispatcher.Query(new GetKillmailsOverviewQuery(characterId), cancellationToken);
        if (!result.IsSuccess)
        {
            return (result, [], []);
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

        IReadOnlyList<KillmailRowViewModel> provisionalRows = _services.GetService<IProvisionalKillmailRepository>() is { } repository
            ? [.. (await repository.GetForCharacterAsync(characterId, cancellationToken)).Select(_BuildProvisionalRow)]
            : [];

        return (result, [.. dtos.Select(_BuildRow)], provisionalRows);
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

    private KillmailRowViewModel _BuildProvisionalRow(ProvisionalKillmail provisional)
    {
        string shipName = _sde.GetType(provisional.VictimShipTypeId)?.Name ?? $"type {provisional.VictimShipTypeId}";
        return new KillmailRowViewModel(provisional, shipName, _clock.LocalTimeZone);
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

    // A row is the way into ET-333's detail screen — the same seam RunsOverviewViewModel._OpenDetailAsync opens
    // ACTIVITY from. The screen reads itself once it is routed (moduleId dedupes a mail opened twice), so nothing is
    // fetched here.
    private Task _OpenDetailAsync(KillmailRowViewModel row)
    {
        _dialogs.ShowKillmailDetail(new KillmailDetailViewModel(_dispatcher, _dialogs, _services, row.CharacterId, row.KillmailId));
        return Task.CompletedTask;
    }

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

        // ET-340: a provisional row is always shown, regardless of SHOW filter or search — it is neither a kill nor
        // a loss to filter by, and it has nothing yet to search on beyond what the badge already says.
        Days.Clear();
        foreach (IGrouping<DateOnly, KillmailRowViewModel> group in scoped.Concat(_provisionalRows)
                     .GroupBy(row => row.Day).OrderByDescending(group => group.Key))
        {
            Days.Add(new KillmailDayViewModel(group.Key, [.. group.OrderByDescending(row => row.KillmailTimeUtc)]));
        }
    }
}
