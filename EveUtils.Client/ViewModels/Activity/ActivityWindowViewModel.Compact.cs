using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Formatting;
using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// The compact run window (ET-478): the same window, read by a smaller view. Nothing here counts anything of its own —
/// the clock, TOTAL ISK, loot, bounty, the run buttons and the follow countdown are the properties and commands the
/// full view binds to, and the figures below only pick the ones the card and the HUD show out of them. What this part
/// owns is which view is showing, which notice is unfolded, and the escalation of this run (read like Runs' OPEN
/// ESCALATIONS band does).
/// </summary>
public sealed partial class ActivityWindowViewModel
{
    private const int CompactHexCount = 3;
    private const int CompactTopItemCount = 5;

    private OpenEscalationRowViewModel? _compactEscalationRow;
    private bool _compactLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullShown))]
    [NotifyPropertyChangedFor(nameof(IsCardShown))]
    [NotifyPropertyChangedFor(nameof(IsHudShown))]
    private bool _isCompact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardShown))]
    [NotifyPropertyChangedFor(nameof(IsHudShown))]
    private CompactRunStyle _compactStyle;

    /// <summary>The notice unfolded in the compact window, or none. A notice that stops being relevant folds itself.</summary>
    [ObservableProperty] private CompactNotice _expandedNotice;

    public bool IsFullShown => !IsCompact;

    public bool IsCardShown => IsCompact && CompactStyle is CompactRunStyle.Card;

    public bool IsHudShown => IsCompact && CompactStyle is CompactRunStyle.Hud;

    // ── The figures ─────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _compactIsRunning;

    [ObservableProperty] private string _compactLootText = string.Empty;

    [ObservableProperty] private string _compactBountyText = string.Empty;

    /// <summary>Site and system for the line under the card and the HUD's hover, marked for OPSEC.</summary>
    [ObservableProperty] private string _compactWhereText = string.Empty;

    [ObservableProperty] private string _compactRosterText = string.Empty;

    [ObservableProperty] private bool _isCompactFleet;

    [ObservableProperty] private string _compactSoloName = string.Empty;

    [ObservableProperty] private string? _compactMoreMembersText;

    [ObservableProperty] private string _compactLootCaption = string.Empty;

    [ObservableProperty] private string? _compactLootMoreText;

    public ObservableCollection<CompactMemberViewModel> CompactMembers { get; } = [];

    public ObservableCollection<CompactLootLineViewModel> CompactTopLoot { get; } = [];

    // ── The notices, there only while they apply ────────────────────────────────────────────────────

    [ObservableProperty] private bool _hasCompactPricingNotice;

    [ObservableProperty] private string _compactPricingChipText = string.Empty;

    [ObservableProperty] private string? _compactPricingDetailText;

    [ObservableProperty] private string? _compactUnrecognisedDetailText;

    [ObservableProperty] private bool _hasCompactAlert;

    [ObservableProperty] private string _compactAlertChipText = string.Empty;

    [ObservableProperty] private string _compactAlertWhereText = string.Empty;

    [ObservableProperty] private string _compactAlertExpiresText = string.Empty;

    [ObservableProperty] private bool _canStartCompactAlert;

    [ObservableProperty] private string? _compactAlertMessage;

    /// <summary>Whether any chip or line of the notices view has something to say: the escalation, the loot that is
    /// missing a price, the tier and weather, why a save failed or why there are no run buttons.</summary>
    public bool HasCompactNoticeContent =>
        HasCompactAlert || HasCompactPricingNotice || NeedsWeatherAndTier || HasRunNotice || IsCommandStatusShown;

    /// <summary>The HUD's second line: the notices, or the countdown that follows the commander's save.</summary>
    public bool HasCompactNotice => HasCompactNoticeContent || IsFollowShown;

    public bool IsAlertNoticeOpen => ExpandedNotice is CompactNotice.OpenEscalation;

    public bool IsPricingNoticeOpen => ExpandedNotice is CompactNotice.LootPricing;

    public bool IsTierNoticeOpen => ExpandedNotice is CompactNotice.TierWeather;

    /// <summary>The ACTIVITY section, whose pickers the tier and weather notice shows.</summary>
    public ActivityWindowSectionViewModel? CompactActivity =>
        _sections.GetValueOrDefault(RunSectionId.Activity) as ActivityWindowSectionViewModel;

    /// <summary>The pilot changed the preferred version in Settings while this window is up.</summary>
    public void UseCompactStyle(CompactRunStyle style) => CompactStyle = style;

    // ── Commands ────────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleCompactAsync()
    {
        IsCompact = !IsCompact;
        await _PersistCompactAsync(CompactRunSettings.CompactKey, IsCompact ? "true" : "false");
    }

    [RelayCommand]
    private void ToggleNotice(CompactNotice notice) =>
        ExpandedNotice = ExpandedNotice == notice ? CompactNotice.None : notice;

    [RelayCommand]
    private async Task StartCompactAlertAsync()
    {
        if (_compactEscalationRow is not { CanStart: true } row
            || _services.GetService<CqrsDispatcher>() is not { } dispatcher
            || _services.GetService<IDialogService>() is not { } dialogs)
            return;

        CompactAlertMessage = await new EscalationRunStarter(dispatcher, dialogs, _services).StartAsync(
            row.Escalation.SourceRunId, row.Escalation.CharacterId, row.CharacterText, row.Escalation.Escalation);
    }

    partial void OnIsCompactChanged(bool value)
    {
        ExpandedNotice = CompactNotice.None;
        _RefreshCompact(DateTime.UtcNow);
    }

    partial void OnExpandedNoticeChanged(CompactNotice value)
    {
        OnPropertyChanged(nameof(IsAlertNoticeOpen));
        OnPropertyChanged(nameof(IsPricingNoticeOpen));
        OnPropertyChanged(nameof(IsTierNoticeOpen));
    }

    // ── Loading and the clock tick ──────────────────────────────────────────────────────────────────

    /// <summary>Which version opens, and whether the window starts in it. Read once per window: a later load (the dialog
    /// service re-reads an open window) must not undo a toggle the pilot made since.</summary>
    private void _LoadCompact(IReadOnlyList<SettingDto>? settings)
    {
        CompactStyle = CompactRunSettings.ReadStyle(settings);
        if (_compactLoaded)
            return;

        _compactLoaded = true;
        IsCompact = CompactRunSettings.StartsCompact(settings);
    }

    /// <summary>Called from <see cref="Refresh"/>: works out the compact window's readout from what the tick has just
    /// settled. Cheap enough to run every second and idle while the full view is up.</summary>
    private void _RefreshCompact(DateTime nowUtc)
    {
        if (!IsCompact)
            return;

        CompactIsRunning = RunState == ActivityRunState.Running;
        CompactWhereText = _CompactWhere();
        CompactBountyText = IskFormat.Whole(_GroupBountyIsk());
        _RefreshCompactMembers();
        _RefreshCompactLoot();
        _RefreshCompactPricing();
        _RefreshCompactAlert(nowUtc);
        _RefreshCompactNoticeFlags();
        _FoldNoticeThatNoLongerApplies();
    }

    private string _CompactWhere()
    {
        string? site = OpsecText.Mark(SignatureName);
        string? system = OpsecText.Mark(SolarSystem);
        return (site, system) switch
        {
            (null, null) => "no site yet",
            (not null, null) => site,
            (null, not null) => system,
            _ => $"{site} · {system}"
        };
    }

    private void _RefreshCompactMembers()
    {
        List<string> names = [.. Participants.Select(participant => participant.CharacterName)
            .Concat(FleetMembers.Select(member => member.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()];
        if (names.Count == 0 && ActingCharacterName is { } acting)
            names = [acting];

        IsCompactFleet = names.Count > 1;
        CompactSoloName = names.FirstOrDefault() ?? "no character yet";
        CompactRosterText = string.Join('\n', names);
        int hidden = names.Count - CompactHexCount;
        CompactMoreMembersText = hidden > 0 ? $"+{hidden}" : null;

        List<CompactMemberViewModel> shown = [.. names.Take(CompactHexCount).Select(name => new CompactMemberViewModel(name))];
        if (shown.Select(member => member.Name).SequenceEqual(CompactMembers.Select(member => member.Name)))
            return;

        CompactMembers.ReconcileTo(shown);
    }

    private void _RefreshCompactLoot()
    {
        if (LootOverview is not { HasCaptures: true } overview)
        {
            CompactLootText = "—";
            CompactLootCaption = "no loot captured";
            CompactLootMoreText = null;
            if (CompactTopLoot.Count > 0)
                CompactTopLoot.Clear();
            return;
        }

        bool isFloor = overview.EntriesWithoutPriceCount > 0 || overview.UnrecognisedCount > 0;
        CompactLootText = (isFloor ? "≥ " : string.Empty) + overview.LootNetIskDisplay;
        int items = overview.CountedItemCount;
        CompactLootCaption = items == 1 ? "TOP ITEMS · 1 ITEM" : $"TOP ITEMS · {items} ITEMS";

        List<CompactLootLineViewModel> top = [.. overview.TopItems(CompactTopItemCount)
            .Select(item => new CompactLootLineViewModel(item.Name, item.Quantity, item.Value))];
        CompactLootMoreText = items > top.Count ? $"+ {items - top.Count} more" : null;
        if (top.Select(line => (line.Name, line.ValueText)).SequenceEqual(CompactTopLoot.Select(line => (line.Name, line.ValueText))))
            return;

        CompactTopLoot.ReconcileTo(top);
    }

    private void _RefreshCompactPricing()
    {
        int unpriced = LootOverview?.EntriesWithoutPriceCount ?? 0;
        int unrecognised = LootOverview?.UnrecognisedCount ?? 0;
        HasCompactPricingNotice = unpriced > 0 || unrecognised > 0;
        CompactPricingChipText = string.Join(" · ", new[]
        {
            unpriced > 0 ? $"{unpriced} NO PRICE" : null,
            unrecognised > 0 ? $"{unrecognised} UNRECOGNISED" : null
        }.OfType<string>());
        CompactPricingDetailText = LootOverview?.LinesWithoutPriceText;
        CompactUnrecognisedDetailText = LootOverview?.UnrecognisedText;
    }

    /// <summary>The escalation this run has registered, the soonest to expire first. Read from what the ACTIVITY section
    /// holds for SAVE — the store has no row of it before that — and worded by the very row Runs' OPEN ESCALATIONS
    /// band uses, so the site, the system and the time left read the same in both places.</summary>
    private void _RefreshCompactAlert(DateTime nowUtc)
    {
        RunEscalationDto? open = CompactActivity?.RegisteredEscalations
            .Where(escalation => escalation.IsOpenAt(nowUtc))
            .OrderBy(escalation => escalation.ExpiresAtUtc ?? DateTime.MaxValue)
            .FirstOrDefault();
        string pilot = ActingCharacterName ?? string.Empty;
        _compactEscalationRow = open is null || RunId is not { } sourceRunId
            ? null
            : new OpenEscalationRowViewModel(
                new OpenEscalationDto(sourceRunId, _ActingCharacterId() ?? 0, pilot, SignatureName, AnchorUtc ?? nowUtc, open, null),
                pilot, nowUtc, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask);

        HasCompactAlert = _compactEscalationRow is not null;
        CompactAlertChipText = _compactEscalationRow is { } row ? $"ESCALATION · {row.ExpiresText}" : string.Empty;
        CompactAlertWhereText = _compactEscalationRow is { } shown
            ? $"{shown.EscalationSiteText} · {shown.EscalationSystemText}"
            : string.Empty;
        CompactAlertExpiresText = _compactEscalationRow?.ExpiresText ?? string.Empty;
        CanStartCompactAlert = _compactEscalationRow?.CanStart ?? false;
    }

    private void _FoldNoticeThatNoLongerApplies()
    {
        bool applies = ExpandedNotice switch
        {
            CompactNotice.OpenEscalation => HasCompactAlert,
            CompactNotice.LootPricing => HasCompactPricingNotice,
            CompactNotice.TierWeather => NeedsWeatherAndTier,
            _ => true
        };
        if (!applies)
            ExpandedNotice = CompactNotice.None;
    }


    // Not observable properties: they are made of flags that change on their own schedule, so they are announced here.
    private void _RefreshCompactNoticeFlags()
    {
        OnPropertyChanged(nameof(HasCompactNoticeContent));
        OnPropertyChanged(nameof(HasCompactNotice));
    }

    private async Task _PersistCompactAsync(string key, string value)
    {
        if (_services.GetService<CqrsDispatcher>() is null)
            return;

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new SetSettingCommand(key, value));
    }
}
