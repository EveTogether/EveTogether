using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// ACTIVITY in the run window: what the run is and where. In an abyssal pocket that is the tier and the weather, the
/// only fields in the window the pilot fills in because nothing can detect them; elsewhere it is the copied site, what
/// the catalogue says about it, and the escalation a site can lead to. Then where the pilot is, and how the run was
/// looted — the label that makes a duration readable.
/// </summary>
public sealed partial class ActivityWindowSectionViewModel : RunWindowSection
{
    /// <summary>Where the manual weather and tier are remembered. Under <c>ui.</c> with the other shell prefs, and
    /// remembered at all because you fly the same tier several runs in a row — which is what turns two clicks a run
    /// into two clicks an evening.</summary>
    public const string WeatherSettingKey = "ui.activity.weather";

    public const string TierSettingKey = "ui.activity.tier";

    /// <summary>Remembered for the same reason as the tier: you loot the same way several runs in a row. One key per
    /// kind, because the kinds loot in different words and a shared key had them overwriting each other's answer.</summary>
    public static string LootStrategySettingKey(ActivityKind kind) =>
        $"ui.activity.lootstrategy.{kind.ToString().ToLowerInvariant()}";

    // What RegisterEscalationAsync collected, carried to SAVE rather than written the moment it is entered: unlike
    // the mission rewards, an escalation is entered mid-run, long before there is a stop time to save against
    // (ET-125). Cleared and rebuilt on every registration — one escalation per run, the last one entered wins.
    private readonly List<RunParameterInput> _escalationParameters = [];

    public ActivityWindowSectionViewModel(IRunWindowContext context)
        : base(context, RunSectionId.Activity, "ACTIVITY")
    {
        IsExpanded = true;
        WeatherChoices = AbyssalWeather.All
            .Select((weather, index) => new ActivityChoice
            {
                Index = index,
                Label = weather.Name,
                Tooltip = $"{weather.EnvironmentName} — {weather.Bonus}, penalty on {weather.PenaltyTarget}"
            })
            .ToList();
        TierChoices = AbyssalTiers.Names
            .Select((tier, index) => new ActivityChoice { Index = index, Label = tier })
            .ToList();
        _lootStrategies = context.RunType.LootStrategies;
        LootStrategyChoices = _ChoicesFor(_lootStrategies);
    }

    /// <summary>The abyssal block instead of the site block: a pocket has a tier and a weather, and no signature.</summary>
    public bool IsInPocket => Context.RunType.Space is RunSpace.AbyssalPocket;

    // ── The pocket ─────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<ActivityChoice> WeatherChoices { get; }

    public IReadOnlyList<ActivityChoice> TierChoices { get; }

    public bool HasWeatherAndTier => Context.HasWeatherAndTier;

    public string TierText => Context.TierText;

    public string WeatherEnvironmentText => Context.Weather?.EnvironmentName ?? "not set";

    public string WeatherEffectText => Context.Weather is { } weather && Context.TierIndex is { } tier
        ? $"{weather.Bonus} · {_PenaltyRange(tier)} {weather.PenaltyTarget}"
        : "not set";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickerShown))]
    private bool _isPickerOpen;

    /// <summary>Twelve buttons are worth the room while the question is open and in the way once it is answered, so
    /// the picker folds behind one line as soon as both halves are set.</summary>
    public bool IsPickerShown => !Context.HasWeatherAndTier || IsPickerOpen;

    [RelayCommand]
    private async Task SelectWeatherAsync(int index)
    {
        Context.WeatherIndex = index;
        _AfterChoice();
        await _PersistAsync(WeatherSettingKey, index.ToString(CultureInfo.InvariantCulture));
        await _AnnounceAbyssalFactsIfCommandingAsync();
    }

    [RelayCommand]
    private async Task SelectTierAsync(int index)
    {
        Context.TierIndex = index;
        _AfterChoice();
        await _PersistAsync(TierSettingKey, index.ToString(CultureInfo.InvariantCulture));
        await _AnnounceAbyssalFactsIfCommandingAsync();
    }

    [RelayCommand]
    private async Task ClearWeatherAndTierAsync()
    {
        Context.WeatherIndex = null;
        Context.TierIndex = null;
        _AfterChoice();
        await _PersistAsync(WeatherSettingKey, string.Empty);
        await _PersistAsync(TierSettingKey, string.Empty);
        await _AnnounceAbyssalFactsIfCommandingAsync();
    }

    /// <summary>Tell the rest of the fleet the pocket's tier or weather just changed (ET-241) — only when this
    /// client commands it and the run is shared, and only the commander's own change: a member picking their own
    /// answer for their own display is not an announcement, or every member's guess would fight over the others'.
    /// </summary>
    private async Task _AnnounceAbyssalFactsIfCommandingAsync()
    {
        if (!Context.IsFleetCommander || Context.FleetId is not { } fleetId || Context.GroupCode is not { } groupCode
            || Context.Services.GetService<IEventBus>() is not { } eventBus)
            return;

        await eventBus.PublishAsync(new FleetRunGroupAbyssalUpdatedEvent(
            new RunGroupAbyssalUpdate(fleetId, Context.Kind, groupCode, Context.TierIndex, Context.Weather?.Name),
            Context.RunCharacterId), EventTarget.Both);
    }

    /// <summary>Reopen the picker on the run that is already answered — the one line it folded behind.</summary>
    [RelayCommand]
    private void OpenPicker() => IsPickerOpen = true;

    // ── The site ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether there is a copied signature behind this run at all. A run started by hand has none, and a
    /// row that can only ever read "not known yet" is worse than no row.</summary>
    public bool HasSignature => Context.SignatureGroup is not null || Context.SignatureName is not null;

    /// <summary>TYPE, from the same catalogue the detail screen and the runs overview read (ET-226) — never "not
    /// known yet" for a kind that already settles it on its own (Mission, Abyssal); only a site with no scanner group
    /// resolves to the catalogue's honest "Site".</summary>
    public string SignatureTypeText => Context.RunType.Name;

    /// <summary>The icon beside <see cref="SignatureTypeText"/>, from the same catalogue row.</summary>
    public MaterialIconKind TypeIcon => Context.RunType.Icon;

    /// <summary>The site, described by what every catalogue match agrees it is — archetype, faction, DED, whether
    /// it turns you away at the gate. Silent about anything they disagree on, and silent about the catalogue
    /// itself: how many rows happen to share an English name is our problem, not the reader's.</summary>
    public string SignatureSiteText => Context.SignatureName is not { } name
        ? "not known yet"
        : SdeSiteDescription.DescribeCommon(Context.MatchedSites) is { Length: > 0 } common
            ? $"{name} — {common}"
            : name;

    /// <summary>The hulls the site lets in, when every match names the same ones — the one fact here that can turn
    /// you away at the gate, so it is stated before you warp rather than discovered after. Null when there is
    /// nothing to add over <see cref="SignatureSiteText"/>, which already carries "ship-restricted" itself.</summary>
    public string? ShipRestrictionText =>
        Context.MatchedSites.Select(_ShipRule).Distinct().ToList() is [{ } only] ? only : null;

    public bool HasShipRestriction => ShipRestrictionText is not null;

    /// <summary>
    /// The hull list behind the site line, on demand. It used to stand inline in this section, where a site like
    /// Blood Lookout ran to thirty-odd names over five lines and pushed LOCATION and LOOT STRATEGY off the bottom
    /// (Raymond, 2026-09-02). The site line still says <c>ship-restricted</c> itself, so the fact of the restriction
    /// never depended on this list being visible.
    /// </summary>
    [RelayCommand]
    private async Task ShowShipRestrictionAsync()
    {
        if (ShipRestrictionText is not { } hulls)
            return;

        await Context.Services.GetRequiredService<IDialogService>()
            .ShowMessageAsync("Ships allowed at this site", hulls);
    }

    /// <summary>What the SDE's own <c>gameplayDescription</c> says about this site (ET-232) — recommended fleet
    /// size, expected time, roles, wherever the catalogue carries one (chiefly homefronts). Shown only when every
    /// match agrees, the same "silent about disagreement" rule <see cref="SignatureSiteText"/> follows.</summary>
    public string? GameplayDescriptionText =>
        Context.MatchedSites.Select(site => site.GameplayDescription).Distinct().ToList() is [{ Length: > 0 } only]
            ? only
            : null;

    public bool HasGameplayDescription => GameplayDescriptionText is not null;

    /// <summary>The gameplay text behind its own link, the same on-demand shape <see cref="ShowShipRestrictionAsync"/>
    /// already uses — a homefront's own text runs to a paragraph or more (fleet size, timer, roles), which crowds
    /// the section exactly as the hull list once did.</summary>
    [RelayCommand]
    private async Task ShowGameplayDescriptionAsync()
    {
        if (GameplayDescriptionText is not { } text)
            return;

        await Context.Services.GetRequiredService<IDialogService>()
            .ShowMessageAsync("What to expect at this site", text);
    }

    // ── The escalation ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Only a type that escalates (ET-124 measured this; an abyssal pocket and a mission do not).</summary>
    public bool IsEscalationRegistrationShown => Context.RunType.Escalates;

    /// <summary>What was last registered this session, or null before the pilot has registered one — shown beside
    /// the button so pressing it again does not read as the only way to tell whether it worked.</summary>
    [ObservableProperty] private string? _escalationRegisteredText;

    /// <summary>
    /// Opens the register-escalation dialog (ET-125) and, on Register, holds the result for SAVE to write. Nothing
    /// here ever supplies a duration on the pilot's behalf — see <see cref="EscalationDialogViewModel"/>'s own
    /// docstring for why (AC-3).
    /// </summary>
    [RelayCommand]
    private async Task RegisterEscalationAsync()
    {
        if (Context.Services.GetService<IDialogService>() is not { } dialogs
            || Context.Services.GetService<ISdeAccessor>() is not { } sde)
            return;

        var dialog = new EscalationDialogViewModel(sde);
        if (!await dialogs.ShowEscalationDialogAsync(dialog) || dialog.Result is not { } result)
            return;

        DateTime nowUtc = DateTime.UtcNow;
        _escalationParameters.Clear();
        _escalationParameters.Add(new RunParameterInput
        {
            ParameterKey = RunParameterKey.Escalation, TypedValue = result.SiteName, ObservedAtUtc = nowUtc
        });
        if (result.DungeonId is { } dungeonId)
            _escalationParameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.EscalationDungeonId,
                TypedValue = dungeonId.ToString(CultureInfo.InvariantCulture),
                ObservedAtUtc = nowUtc
            });
        _escalationParameters.Add(new RunParameterInput
        {
            ParameterKey = RunParameterKey.EscalationSystem, TypedValue = result.DestinationSystem, ObservedAtUtc = nowUtc
        });
        if (result.DestinationSolarSystemId is { } destinationSolarSystemId)
            _escalationParameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.EscalationSolarSystemId,
                TypedValue = destinationSolarSystemId.ToString(CultureInfo.InvariantCulture),
                ObservedAtUtc = nowUtc
            });
        _escalationParameters.Add(new RunParameterInput
        {
            ParameterKey = RunParameterKey.EscalationExpiresAtUtc,
            TypedValue = result.ExpiresAtUtc.ToString("o", CultureInfo.InvariantCulture),
            ObservedAtUtc = nowUtc
        });
        EscalationRegisteredText = $"{result.SiteName} · {result.DestinationSystem}";
    }

    // ── Where ──────────────────────────────────────────────────────────────────────────────────────

    public string LocationText => Context.IsInsideAbyssal
        ? "none — an abyssal pocket has no location"
        : (Context.LocationDisplay ?? Context.SolarSystem) is { } place
            // Never behind "not known yet": that line is about us rather than about where he is, and a scan id in
            // brackets after it would read as half a place.
            ? Context.SignatureId is { Length: > 0 } signature ? $"{place} ({signature})" : place
            : "not known yet";

    /// <summary>Shown only once there is a system to show. "not known yet" is a line about us, not about where he
    /// is, and the row is hidden instead.</summary>
    public bool IsLocationShown =>
        Context.IsInsideAbyssal || Context.LocationDisplay is not null || Context.SolarSystem is not null;

    // ── The loot strategy ──────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<RunLootStrategy> _lootStrategies;

    /// <summary>What was looted and what was left. Without it "19 minutes" says nothing — which is why it is set
    /// here rather than only displayed.</summary>
    [ObservableProperty] private RunLootStrategy? _lootStrategy;

    /// <summary>The list this run's type loots by (<see cref="RunTypeDefinition.LootStrategies"/>).</summary>
    public IReadOnlyList<RunLootStrategy> LootStrategies => _lootStrategies;

    public IReadOnlyList<ActivityChoice> LootStrategyChoices { get; private set; }

    /// <summary>Hidden rather than shown empty: a LOOT STRATEGY row with no buttons under it reads as a question
    /// the window failed to load, not as one that does not apply here.</summary>
    public bool IsLootStrategyShown => LootStrategyChoices.Count > 0;

    /// <summary>Pressing the strategy that is already set clears it — the row has no other way back to unset, and
    /// a wrong label on a saved run is worse than none.</summary>
    [RelayCommand]
    private async Task SelectLootStrategyAsync(int index)
    {
        LootStrategy = LootStrategy == LootStrategies[index] ? null : LootStrategies[index];
        _SyncChoices();
        Context.Refresh(DateTime.UtcNow);
        await _PersistAsync(LootStrategySettingKey(Context.Kind), LootStrategy?.ToString() ?? string.Empty);
        // Onto the run the moment it is pressed, so a run the app finishes by itself (ET-179) keeps the answer. A
        // strategy chosen before START has no row yet and reaches the run through SAVE.
        if (Context.RunId is { } runId)
        {
            using var scope = Context.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Send(new SetRunLootStrategyCommand(runId, LootStrategy));
        }
    }

    // ── The window's hooks ─────────────────────────────────────────────────────────────────────────

    public override void Load(IReadOnlyList<SettingDto>? settings)
    {
        if (settings is not null)
        {
            // Only where nothing has claimed either fact yet: a member who joined on the fleet commander's own tier
            // and weather (ET-241, JoinFleetRun) keeps it — this is the last-remembered fallback for a window with
            // neither, not a value that overrides an answer already known to be real.
            Context.WeatherIndex ??= _Restore(settings.FirstOrDefault(s => s.Key == WeatherSettingKey)?.Value,
                AbyssalWeather.All.Count);
            Context.TierIndex ??= _Restore(settings.FirstOrDefault(s => s.Key == TierSettingKey)?.Value,
                AbyssalTiers.Names.Count);

            // A remembered strategy this type does not loot by addresses nothing here, so it reads as unset — the
            // same rule the two indices get.
            string? remembered = settings.FirstOrDefault(s => s.Key == LootStrategySettingKey(Context.Kind))?.Value;
            LootStrategy = LootStrategies.Cast<RunLootStrategy?>()
                .FirstOrDefault(candidate => candidate.ToString() == remembered);
        }

        _SyncChoices();
    }

    public override void RefreshSummary()
    {
        if (!IsInPocket)
        {
            HeaderSummary = string.Join(" · ",
                new[] { Context.SignatureName ?? "no signature", _ShortDemand(), Context.SolarSystem }
                    .Where(part => part is not null));
            return;
        }

        HeaderSummary = Context.Weather is { } weather && Context.TierIndex is { } tier
            ? $"{AbyssalTiers.Names[tier]} T{tier} · {weather.Name} · no location"
            : "not set yet · no location";
    }

    /// <summary>The loot strategy and the pocket's own tier and weather go with every run of the group — everybody
    /// flew the same instance; the escalation only with the one it was registered on.</summary>
    public override void AddToSave(RunSaveDraft draft)
    {
        draft.LootStrategy = LootStrategy;
        if (draft.IsActingRun)
            draft.Parameters.AddRange(_escalationParameters);

        // Never lost at save (ET-241): the window only ever held this in Context.WeatherIndex/TierIndex, with no
        // column and no RunParameter row until now — measured in a real run's database as zero rows before this.
        if (Context.HasWeatherAndTier)
            draft.Parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.AbyssalFilament,
                TypedValue = $"{Context.TierIndex}|{Context.Weather!.Name}",
                ObservedAtUtc = DateTime.UtcNow
            });
    }

    protected override void OnContextChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(IRunWindowContext.RunType):
                _OnRunTypeChanged();
                break;
            case nameof(IRunWindowContext.SignatureGroup):
                OnPropertyChanged(nameof(HasSignature));
                break;
            case nameof(IRunWindowContext.SignatureName):
                OnPropertyChanged(nameof(SignatureSiteText));
                OnPropertyChanged(nameof(HasSignature));
                break;
            case nameof(IRunWindowContext.MatchedSites):
                OnPropertyChanged(nameof(SignatureSiteText));
                OnPropertyChanged(nameof(ShipRestrictionText));
                OnPropertyChanged(nameof(HasShipRestriction));
                break;
            case nameof(IRunWindowContext.SignatureId):
                OnPropertyChanged(nameof(LocationText));
                break;
            case nameof(IRunWindowContext.SolarSystem) or nameof(IRunWindowContext.LocationDisplay)
                or nameof(IRunWindowContext.IsInsideAbyssal):
                OnPropertyChanged(nameof(LocationText));
                OnPropertyChanged(nameof(IsLocationShown));
                break;
            case nameof(IRunWindowContext.WeatherIndex):
                OnPropertyChanged(nameof(WeatherEnvironmentText));
                OnPropertyChanged(nameof(WeatherEffectText));
                break;
            case nameof(IRunWindowContext.TierIndex):
                OnPropertyChanged(nameof(TierText));
                OnPropertyChanged(nameof(WeatherEffectText));
                break;
            case nameof(IRunWindowContext.HasWeatherAndTier):
                OnPropertyChanged(nameof(HasWeatherAndTier));
                OnPropertyChanged(nameof(IsPickerShown));
                break;
        }
    }

    /// <summary>A window whose type changes mid-run — a group copied after the start — reads its type's facts again.
    /// The strategy list is only rebuilt when it is a different list, so the site types, which share one, never lose
    /// the chip that is already pressed.</summary>
    private void _OnRunTypeChanged()
    {
        OnPropertyChanged(nameof(IsInPocket));
        OnPropertyChanged(nameof(SignatureTypeText));
        OnPropertyChanged(nameof(TypeIcon));
        OnPropertyChanged(nameof(HasSignature));
        OnPropertyChanged(nameof(IsEscalationRegistrationShown));
        if (ReferenceEquals(_lootStrategies, Context.RunType.LootStrategies))
            return;

        _lootStrategies = Context.RunType.LootStrategies;
        LootStrategyChoices = _ChoicesFor(_lootStrategies);
        if (LootStrategy is { } chosen && !_lootStrategies.Contains(chosen))
            LootStrategy = null;
        _SyncChoices();
        OnPropertyChanged(nameof(LootStrategies));
        OnPropertyChanged(nameof(LootStrategyChoices));
        OnPropertyChanged(nameof(IsLootStrategyShown));
    }

    private void _AfterChoice()
    {
        IsPickerOpen = !Context.HasWeatherAndTier;
        _SyncChoices();
        Context.Refresh(DateTime.UtcNow);
    }

    /// <summary>Mirror the selection onto the buttons. The choices carry it themselves so the picker can be a flat
    /// <c>ItemsControl</c> instead of five and seven hand-written buttons.</summary>
    private void _SyncChoices()
    {
        foreach (ActivityChoice choice in WeatherChoices)
            choice.IsSelected = choice.Index == Context.WeatherIndex;

        foreach (ActivityChoice choice in TierChoices)
            choice.IsSelected = choice.Index == Context.TierIndex;

        foreach (ActivityChoice choice in LootStrategyChoices)
            choice.IsSelected = LootStrategy is { } chosen && LootStrategies[choice.Index] == chosen;
    }

    private async Task _PersistAsync(string key, string value)
    {
        using var scope = Context.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new SetSettingCommand(key, value));
    }

    private static IReadOnlyList<ActivityChoice> _ChoicesFor(IReadOnlyList<RunLootStrategy> strategies) =>
        strategies.Select((strategy, index) => new ActivityChoice { Index = index, Label = _LabelOf(strategy) }).ToList();

    /// <summary>The words on the chips. They live here and not in the stored value, so rewording one never reaches a
    /// run that was already saved.</summary>
    private static string _LabelOf(RunLootStrategy strategy) => strategy switch
    {
        RunLootStrategy.BioadaptiveOnly => "bioadaptive only",
        RunLootStrategy.BioadaptiveAndTriglavian => "bioadaptive + triglavian",
        RunLootStrategy.AllCans => "all cans",
        RunLootStrategy.Blitzed => "blitzed",
        RunLootStrategy.Cleared => "cleared",
        RunLootStrategy.FullClear => "full clear",
        RunLootStrategy.CherryPicked => "cherry-picked",
        // A run written by a newer build: its own name beats an empty chip.
        _ => strategy.ToString()
    };

    /// <summary>The shut header carries what the run demands, not only what it is called — the same description the
    /// site line and the toast use, so the three cannot drift apart. Silent when the entries sharing the name do
    /// not agree: a demand is worth nothing if it might be the neighbour's.</summary>
    private string? _ShortDemand() =>
        SdeSiteDescription.DescribeCommon(Context.MatchedSites) is { Length: > 0 } common ? common : null;

    /// <summary>The hulls a site names, or null when it names none. A restricted site whose allow-list resolves to
    /// no groups and no individual hulls says nothing here and stays "ship-restricted" on the site line — reading it
    /// as "anything goes" is the one mistake here that costs a ship.
    ///
    /// Individually included hulls (ET-232) are the refinement a group alone cannot express — a homefront's T1-only
    /// cruisers are 16 named types, not the whole Cruiser group, so they never show up in
    /// <see cref="SdeSite.AllowedShipGroups"/> at all. An individually excluded hull is dropped from that list
    /// unconditionally, even one that would otherwise pass through an allowed group: it is never shown as allowed
    /// (ET-232 AC-2), and an include without a matching exclude simply widens what the groups already say.</summary>
    private static string? _ShipRule(SdeSite site)
    {
        if (!site.IsShipRestricted)
            return null;

        HashSet<int> excludedTypeIds = [.. site.ExcludedShipTypes.Select(type => type.TypeId)];
        string[] names =
        [
            .. site.AllowedShipGroups.Select(group => group.Name)
                .Concat(site.IncludedShipTypes.Where(type => !excludedTypeIds.Contains(type.TypeId)).Select(type => type.Name))
                .Distinct()
                .Order()
        ];
        return names.Length == 0 ? null : string.Join(", ", names);
    }

    /// <summary>The resist penalty is rolled per site rather than fixed per tier, so the window shows the band it
    /// can land in instead of a number it would be inventing — the same three strengths AbyssalBeacons offers as an
    /// explicit choice.</summary>
    private static string _PenaltyRange(int tier) => tier <= 3 ? "-30% or -50%" : "-50% or -70%";

    /// <summary>A stored index that no longer addresses anything is treated as unset — the alternative is a window
    /// that throws on open because a list got shorter.</summary>
    private static int? _Restore(string? stored, int count) =>
        int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
        && index >= 0 && index < count
            ? index
            : null;
}
