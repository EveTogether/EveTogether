using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;
using SkillQueueStanding = EveUtils.Client.ViewModels.Home.SkillQueueStanding;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>
/// SKILLS module (ET-16): one screen, one character at a time, picked from the header — CATALOGUE and TRAINING
/// QUEUE are built. PLANS and OPTIMISE are placeholder tabs; their content lands in S5/S6.
/// </summary>
public sealed partial class SkillsWindowViewModel : ObservableObject, IRefreshableModule
{
    public const string LastCharacterSettingKey = "skills.last-character";

    private readonly ICharacterRegistry _registry;
    private readonly ICharacterSkillRepository _skillRepository;
    private readonly ICharacterSkillQueueRepository _queueRepository;
    private readonly ICharacterAttributesRepository _attributesRepository;
    private readonly ISdeAccessor _sde;
    private readonly ISettingRepository? _settings;
    private readonly ICharacterPortraitProvider? _portraits;
    private readonly int? _startingCharacterId;

    private IReadOnlyList<Character> _characters = [];
    private bool _suppressSelectionApply; // set while _SelectCharacterAsync syncs SelectedCharacterOption back onto itself
    private int _selectionVersion; // bumped on every _SelectCharacterAsync call; a stale call discards its result on completion

    [ObservableProperty] private int? _selectedCharacterId;
    [ObservableProperty] private string _selectedCharacterName = "";
    [ObservableProperty] private string _characterOrdinalText = "";
    [ObservableProperty] private string _totalSpText = "—";
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private SkillsCatalogueViewModel? _catalogue;
    [ObservableProperty] private SkillsQueueViewModel? _queue;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Every character, for the header ComboBox — the ET-184 row (hex, name, "SP · queue", radio).</summary>
    public ObservableCollection<CharacterPickRowViewModel> CharacterOptions { get; } = [];

    /// <summary><see cref="CharacterOptions"/> narrowed by <see cref="CharacterSearchText"/> — what the ComboBox's
    /// dropdown actually lists (ET-16 D-point-8, shared filter with ET-184's own CharacterPickerWindow).</summary>
    public ObservableCollection<CharacterPickRowViewModel> FilteredCharacterOptions { get; } = [];

    [ObservableProperty] private CharacterPickRowViewModel? _selectedCharacterOption;
    [ObservableProperty] private string _characterSearchText = "";

    /// <summary>The search field only earns its place once scrolling stops being the faster way to find a name
    /// (ET-16 AC1 / D-point-8).</summary>
    public bool ShowCharacterSearch => CharacterOptions.Count >= CharacterPickerSearch.SearchThreshold;

    public string CharacterSearchWatermark => $"Search {CharacterOptions.Count} characters…";

    /// <param name="startingCharacterId">The character to open on — the pilot row on HOME this was launched from
    /// (ET-16 AC2). Null when launched from the rail, which falls back to the last character this module was left
    /// on, or the first in the character column's own order.</param>
    public SkillsWindowViewModel(IServiceProvider services, int? startingCharacterId)
    {
        _registry = services.GetRequiredService<ICharacterRegistry>();
        _skillRepository = services.GetRequiredService<ICharacterSkillRepository>();
        _queueRepository = services.GetRequiredService<ICharacterSkillQueueRepository>();
        _attributesRepository = services.GetRequiredService<ICharacterAttributesRepository>();
        _sde = services.GetRequiredService<ISdeAccessor>();
        _settings = services.GetService<ISettingRepository>();
        _portraits = services.GetService<ICharacterPortraitProvider>();
        _startingCharacterId = startingCharacterId;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _characters = await _registry.GetAllAsync(cancellationToken); // already in the character column's SortOrder
        if (_characters.Count == 0)
        {
            StatusMessage = "No characters yet — add one from the character column.";
            return;
        }

        await _BuildCharacterOptionsAsync(cancellationToken);

        // A refresh (RefreshModule, ET-48 "route to existing") must keep showing whatever character is already on
        // screen, not snap back to the one this instance first opened on — only the very first load resolves a
        // starting character at all.
        int characterId = SelectedCharacterId is { } current && _characters.Any(c => c.EsiCharacterId == current)
            ? current
            : await _ResolveStartingCharacterIdAsync(cancellationToken);
        await _SelectCharacterAsync(characterId, cancellationToken);
    }

    /// <inheritdoc/>
    public void RefreshModule() => _ = LoadAsync();

    /// <summary>Switches to a character on an already-open instance (ET-16 AC2) — the module-reuse counterpart to
    /// the constructor's <c>startingCharacterId</c>, which only applies on a fresh open.</summary>
    public Task GoToCharacterAsync(int characterId) => _SelectCharacterAsync(characterId, CancellationToken.None);

    partial void OnCharacterSearchTextChanged(string value)
    {
        FilteredCharacterOptions.Clear();
        foreach (var option in CharacterOptions)
            if (CharacterPickerSearch.Matches(option, value))
                FilteredCharacterOptions.Add(option);
        // The current pick stays listed even if the filter would hide it — a ComboBox clears SelectedItem the
        // moment it drops out of ItemsSource, which would silently desync the dropdown's own radio mark from what
        // the header actually shows.
        if (SelectedCharacterOption is { } selected && !FilteredCharacterOptions.Contains(selected))
            FilteredCharacterOptions.Insert(0, selected);
    }

    // The header ComboBox's own selection (a real Avalonia ComboBox, D-point: "closes on selection, that's what a
    // ComboBox does by default" — no bespoke popup/close logic here).
    partial void OnSelectedCharacterOptionChanged(CharacterPickRowViewModel? value)
    {
        if (_suppressSelectionApply || value is null || value.CharacterId == SelectedCharacterId)
            return;
        _ = _SelectCharacterAsync(value.CharacterId, CancellationToken.None);
    }

    private async Task _BuildCharacterOptionsAsync(CancellationToken cancellationToken)
    {
        CharacterOptions.Clear();
        foreach (var character in _characters)
        {
            int characterId = character.EsiCharacterId ?? 0;
            var attributes = await _attributesRepository.GetAsync(characterId, cancellationToken);
            var queueEntries = await _queueRepository.GetForCharacterAsync(characterId, cancellationToken);
            var standing = SkillQueueStanding.From(
                queueEntries.Where(e => e.FinishDate is null || e.FinishDate > DateTimeOffset.UtcNow).ToList(),
                _ => ""); // only Queued/IsPaused are read below — the head skill's name is not shown in this row

            string spText = attributes is { } a ? $"{(a.TotalSp / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)}M SP" : "— SP";
            string queueText = standing is null ? "no queue"
                : standing.IsPaused ? "queue paused"
                : $"queue {SkillQueueStanding.Until(standing.QueueLeft(DateTimeOffset.UtcNow) ?? TimeSpan.Zero)}";

            var row = new CharacterPickRowViewModel(new CharacterPickOption(characterId, character.Name, $"{spText} · {queueText}", true));
            CharacterOptions.Add(row);
            // Best-effort, same as every other CharacterPickRowViewModel consumer (CharacterPickerWindow,
            // FleetInviteWindow): a portrait that never loads just leaves the row on its initial-glyph fallback.
            if (_portraits is not null)
                _ = row.LoadPortraitAsync(_portraits, cancellationToken);
        }
        OnCharacterSearchTextChanged(CharacterSearchText); // (re)builds FilteredCharacterOptions
        OnPropertyChanged(nameof(ShowCharacterSearch));
        OnPropertyChanged(nameof(CharacterSearchWatermark));
    }

    private async Task<int> _ResolveStartingCharacterIdAsync(CancellationToken cancellationToken)
    {
        if (_startingCharacterId is { } given && _characters.Any(c => c.EsiCharacterId == given))
            return given;

        if (_settings is not null)
        {
            var remembered = (await _settings.ListAsync(cancellationToken))
                .FirstOrDefault(s => s.Key == LastCharacterSettingKey)?.Value;
            if (remembered is not null && int.TryParse(remembered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastId)
                && _characters.Any(c => c.EsiCharacterId == lastId))
                return lastId;
        }

        return _characters[0].EsiCharacterId ?? 0;
    }

    private async Task _SelectCharacterAsync(int characterId, CancellationToken cancellationToken)
    {
        // A header pick, a RefreshModule reload and an explicit GoToCharacterAsync (ET-48 "route to existing") can
        // all reach here for the same instance close together. Every read below is off the UI thread and takes
        // real time, so two calls can overlap; the one whose version this method still owns when it is done is the
        // one that gets to apply its result — the same "a read version stamp discards a superseded result" rule
        // KillmailsOverviewViewModel holds itself to for a fast character switch (ET-332).
        int version = ++_selectionVersion;
        IsLoading = true;
        try
        {
            var character = _characters.FirstOrDefault(c => c.EsiCharacterId == characterId);
            if (character is null)
                return;

            var snapshot = await _BuildSnapshotAsync(characterId, cancellationToken);
            // The SDE reads inside both view-models are synchronous SQLite queries — off the UI thread, the same
            // rule RunsOverviewViewModel and KillmailsOverviewViewModel hold themselves to for their own reads.
            var (catalogue, queue) = await Task.Run(() =>
                (new SkillsCatalogueViewModel(snapshot), new SkillsQueueViewModel(snapshot)), cancellationToken);

            if (version != _selectionVersion)
                return; // superseded while reading — the newer call's result is what the screen should show

            SelectedCharacterId = characterId;
            SelectedCharacterName = character.Name;
            int ordinal = _characters.ToList().FindIndex(c => c.EsiCharacterId == characterId) + 1;
            CharacterOrdinalText = $"{ordinal} of {_characters.Count}";

            _suppressSelectionApply = true;
            foreach (var option in CharacterOptions)
                option.IsSelected = option.CharacterId == characterId;
            SelectedCharacterOption = CharacterOptions.FirstOrDefault(o => o.CharacterId == characterId);
            _suppressSelectionApply = false;

            TotalSpText = snapshot.Attributes is { } attrs
                ? $"{attrs.TotalSp.ToString("N0", CultureInfo.InvariantCulture)} skill points"
                : "—"; // AC6: straight from ESI total_sp — never a sum over trained levels
            Catalogue = catalogue;
            Queue = queue;
            StatusMessage = null;

            if (_settings is not null)
                await _settings.UpsertAsync(LastCharacterSettingKey, characterId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<SkillsCharacterSnapshot> _BuildSnapshotAsync(int characterId, CancellationToken cancellationToken)
    {
        var levels = await _skillRepository.GetLevelsAsync(characterId, cancellationToken);
        var queue = await _queueRepository.GetForCharacterAsync(characterId, cancellationToken);
        var attributes = await _attributesRepository.GetAsync(characterId, cancellationToken);
        return new SkillsCharacterSnapshot(_sde, levels, queue, attributes, DateTimeOffset.UtcNow);
    }
}
