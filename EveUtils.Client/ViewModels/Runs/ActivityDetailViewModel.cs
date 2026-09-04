using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using ActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// ET-162: one saved activity, fully expanded — the unfolded brother of the activity window, reusing its
/// <see cref="ActivitySection"/> shape rather than putting a second look beside it.
///
/// The screen's own subject is which sections an activity kind has, and what a missing one says. Two rules, and
/// they are different on purpose:
/// <list type="bullet">
/// <item>A section the kind claims is always drawn. Empty, it carries one line naming what was not measured —
/// never a "0", which reads as a measurement that was taken and came out at nothing.</item>
/// <item>A section the kind does not claim is not drawn, and is named in <see cref="AbsentSectionsText"/> with the
/// reason. Dropping it silently would leave the reader unable to tell "there was nothing" from "this kind has no
/// such thing".</item>
/// </list>
/// A kind that does not claim a section still gets it when there is data for it, so a reward booked against a site
/// run cannot disappear behind the table.
/// </summary>
public sealed partial class ActivityDetailViewModel : ViewModelBase, IRefreshableModule
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IMarketPriceRepository? _prices;
    private readonly Guid _activitySummaryId;

    public ActivityDetailViewModel(CqrsDispatcher dispatcher, Guid activitySummaryId,
        IMarketPriceRepository? prices = null)
    {
        _dispatcher = dispatcher;
        _activitySummaryId = activitySummaryId;
        _prices = prices;
    }

    public ActivitySection Activity { get; } = new() { Title = "ACTIVITY", IsExpanded = true };
    public ActivitySection Rewards { get; } = new() { Title = "REWARDS", IsExpanded = true };
    public ActivitySection Enemies { get; } = new() { Title = "ENEMIES", IsExpanded = true };
    public ActivitySection Fleet { get; } = new() { Title = "FLEET", IsExpanded = true };
    public ActivitySection Bounty { get; } = new() { Title = "BOUNTY", IsExpanded = true };
    public ActivitySection Loot { get; } = new() { Title = "LOOT", IsExpanded = true };
    public ActivitySection Escalation { get; } = new() { Title = "ESCALATION", IsExpanded = true };

    public ObservableCollection<ActivityRewardRowViewModel> RewardRows { get; } = [];
    public ObservableCollection<ActivityEnemyRowViewModel> EnemyRows { get; } = [];
    public ObservableCollection<ActivityRunRowViewModel> RunRows { get; } = [];
    public ObservableCollection<ActivityLootCaptureRowViewModel> LootCaptureRows { get; } = [];

    [ObservableProperty] private string _siteText = string.Empty;
    [ObservableProperty] private string _kindText = string.Empty;
    [ObservableProperty] private string _durationText = string.Empty;
    [ObservableProperty] private string _startText = string.Empty;
    [ObservableProperty] private string _endText = string.Empty;

    /// <summary>Whether the duration above it was measured or typed. The corrected moments overwrite the start and
    /// stop, so the figure itself can no longer say which of the two it is (ET-98).</summary>
    [ObservableProperty] private string _timeSourceText = string.Empty;

    /// <summary>Why there is nothing on screen, when there is nothing — a failed lookup is a state, not silence.</summary>
    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty] private bool _isAgentShown;
    [ObservableProperty] private string _agentText = string.Empty;
    [ObservableProperty] private string _missionLevelText = string.Empty;
    [ObservableProperty] private string _locationText = string.Empty;
    [ObservableProperty] private string _signatureText = string.Empty;
    [ObservableProperty] private bool _isSignatureShown;
    [ObservableProperty] private string _fitText = string.Empty;
    [ObservableProperty] private string? _objectivesText;
    [ObservableProperty] private bool _hasObjectives;

    [ObservableProperty] private bool _isRewardsShown;
    [ObservableProperty] private bool _isBountyShown;
    [ObservableProperty] private bool _isLootShown;
    [ObservableProperty] private bool _isEscalationShown;

    /// <summary>The sections this kind does not have, each with the reason. The distinguishing line of the screen:
    /// an absent section that says nothing is indistinguishable from an empty one.</summary>
    [ObservableProperty] private string? _absentSectionsText;

    [ObservableProperty] private string? _rewardsEmptyText;
    [ObservableProperty] private string? _enemiesEmptyText;
    [ObservableProperty] private string? _bountyEmptyText;
    [ObservableProperty] private string? _lootEmptyText;
    [ObservableProperty] private string? _escalationEmptyText;

    [ObservableProperty] private bool _hasBountyFigures;
    [ObservableProperty] private string _bountyText = string.Empty;

    [ObservableProperty] private bool _hasLootFigures;
    [ObservableProperty] private string _lootIskText = string.Empty;
    [ObservableProperty] private string _consumedIskText = string.Empty;
    [ObservableProperty] private string _netIskText = string.Empty;

    /// <summary>Counted, not hidden: a row the lookup has no price for is not worth nothing, it is worth something
    /// nobody has told us (ET-159 AC-2).</summary>
    [ObservableProperty] private string? _linesWithoutPriceText;

    [ObservableProperty] private string _participantCountText = string.Empty;
    [ObservableProperty] private string _fleetBasisText = string.Empty;

    [ObservableProperty] private string? _escalationText;
    [ObservableProperty] private string? _escalationObservedText;

    public void RefreshModule() => _ = LoadAsync();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Result<ActivityDetailDto> detail =
            await _dispatcher.Query(new GetActivityDetailQuery(_activitySummaryId), cancellationToken);
        if (!detail.IsSuccess || detail.Value is null)
        {
            StatusMessage = detail.Messages.Count > 0 ? detail.Messages[0].Text : "The activity could not be read.";
            return;
        }

        StatusMessage = null;
        IReadOnlyDictionary<int, decimal> unitPrices = await _UnitPricesAsync(detail.Value, cancellationToken);
        _Apply(detail.Value, unitPrices);
    }

    /// <summary>
    /// Values every loot line by type id through ET's own price cache — the same lookup
    /// <c>RebuildActivitySummariesCommandHandler</c> used to build the totals this screen shows, so a line and the
    /// total above it cannot tell different stories. Never <see cref="RunLootEntryDto.ClipboardPrice"/>: that column
    /// is kept as what the pilot's inventory window happened to show and is never held for a valuation.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, decimal>> _UnitPricesAsync(
        ActivityDetailDto detail, CancellationToken cancellationToken)
    {
        if (_prices is null)
            return new Dictionary<int, decimal>();

        List<int> typeIds = [.. detail.Runs
            .SelectMany(run => run.LootCaptures)
            .SelectMany(capture => capture.Entries)
            .Select(entry => entry.ItemTypeId)
            .Distinct()];
        if (typeIds.Count == 0)
            return new Dictionary<int, decimal>();

        IReadOnlyDictionary<int, double> averages = await _prices.GetAveragePricesAsync(typeIds, cancellationToken);
        return averages.ToDictionary(price => price.Key, price => (decimal)price.Value);
    }

    private void _Apply(ActivityDetailDto detail, IReadOnlyDictionary<int, decimal> unitPrices)
    {
        _ApplyHeader(detail);
        _ApplyActivity(detail);
        _ApplyRewards(detail);
        _ApplyEnemies(detail);
        _ApplyFleet(detail);
        _ApplyBounty(detail);
        _ApplyLoot(detail, unitPrices);
        _ApplyEscalation(detail);
        _ApplySectionsPerKind(detail.ActivityKind);
    }

    private void _ApplyHeader(ActivityDetailDto detail)
    {
        SiteText = detail.SiteName ?? "site not recorded";
        KindText = _KindLabel(detail.ActivityKind);
        DurationText = TimeSpan.FromSeconds(detail.DurationSeconds).ToString(@"hh\:mm\:ss");
        StartText = detail.StartedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        EndText = detail.StoppedAtUtc is { } stoppedAtUtc
            ? stoppedAtUtc.ToLocalTime().ToString("HH:mm:ss")
            : "still open";
        TimeSourceText = detail.Runs.Any(run => run.TimesCorrectedAtUtc is not null)
            ? "times corrected by hand"
            : "measured";
    }

    private void _ApplyActivity(ActivityDetailDto detail)
    {
        ActivityRunDetailDto? source = detail.Runs.FirstOrDefault();
        // Only a mission carries an agent, so the row is not there for anything else — a line that could only ever
        // read "not applicable" teaches the reader about our catalogue, not about their run.
        IsAgentShown = detail.ActivityKind == ActivityKind.Mission;
        ActivityRunDetailDto? withAgent = detail.Runs.FirstOrDefault(run => run.AgentId is not null);
        AgentText = withAgent?.AgentId is { } agentId ? $"agent {agentId}" : "not recorded";
        MissionLevelText = withAgent?.MissionLevel is { } level ? $"level {level}" : "not recorded";
        LocationText = detail.SolarSystemId is { } solarSystemId ? $"system {solarSystemId}" : "not recorded";
        SignatureText = source?.Signature ?? string.Empty;
        IsSignatureShown = !string.IsNullOrWhiteSpace(source?.Signature);
        FitText = source?.FitNameSnapshot ?? "not recognised";

        // Mission counters, and only those: the reward forms belong under REWARDS, the escalation under its own
        // section. They stay empty until somebody types them — nothing plausible is filled in for them.
        string[] objectives = [.. detail.Parameters
            .Where(parameter => parameter.ParameterKey is RunParameterKey.Smugglers or RunParameterKey.Civilians)
            .Select(parameter => $"{parameter.ParameterKey.ToString().ToLowerInvariant()} {parameter.TypedValue}")];
        HasObjectives = objectives.Length > 0;
        ObjectivesText = HasObjectives ? string.Join(" · ", objectives) : null;

        Activity.HeaderSummary = detail.ActivityKind == ActivityKind.Mission
            ? $"{AgentText} · {MissionLevelText}"
            : $"{KindText} · {LocationText}";
    }

    private void _ApplyRewards(ActivityDetailDto detail)
    {
        RewardRows.Clear();
        // Everything that is not claimed by another section, rather than a list of the keys known when this was
        // written: RunParameterKey only ever grows, and a key nobody special-cased must show up rather than vanish.
        foreach (RunParameterDto parameter in detail.Parameters.Where(_IsRewardParameter))
            RewardRows.Add(new ActivityRewardRowViewModel(parameter));

        RewardsEmptyText = RewardRows.Count > 0 ? null : "No reward was recorded for this activity.";
        Rewards.HeaderSummary = RewardRows.Count > 0
            ? string.Join(" · ", RewardRows.Select(row => $"{row.ValueText} {row.Label}"))
            : "nothing recorded";
    }

    private void _ApplyEnemies(ActivityDetailDto detail)
    {
        EnemyRows.Clear();
        foreach (RunEnemyObservationDto observation in detail.EnemyObservations)
            EnemyRows.Add(new ActivityEnemyRowViewModel(observation));

        EnemiesEmptyText = EnemyRows.Count > 0
            ? null
            : "No combat line came past in the game log for this activity. That is a measurement, not an empty list.";
        Enemies.HeaderSummary = EnemyRows.Count > 0
            ? $"{detail.EnemyObservations.Sum(observation => observation.Count)} counted · " +
              $"{detail.EnemyObservations.Select(observation => observation.EnemyTypeId).Distinct().Count()} types"
            : "no combat measured";
    }

    private void _ApplyFleet(ActivityDetailDto detail)
    {
        RunRows.Clear();
        foreach (ActivityRunDetailDto run in detail.Runs)
            RunRows.Add(new ActivityRunRowViewModel(run));

        // The count is the summary's, over distinct characters — that figure is real. Only the names are missing,
        // and saying so beats a blank block that leaves the reader guessing which of the two it is looking at.
        ParticipantCountText = $"{detail.ParticipantCount} participants";
        FleetBasisText = "Participant names are not recorded yet, so these are the runs behind this activity by " +
                         "character id. The count above is real: it comes from the activity's own distinct characters.";
        Fleet.HeaderSummary = $"{ParticipantCountText} · {detail.PayoutEligibleCount} sharing";
    }

    private void _ApplyBounty(ActivityDetailDto detail)
    {
        // Not "0 ISK": BountyIsk is zero both when nothing was shot and when nothing was measured, and only the
        // absence of bounty rows tells those apart.
        HasBountyFigures = detail.BountyEntries.Count > 0;
        BountyText = $"{detail.BountyIsk:N2} ISK";
        BountyEmptyText = HasBountyFigures
            ? null
            : "No bounty line came past in the game log for this activity.";
        Bounty.HeaderSummary = HasBountyFigures
            ? $"{BountyText} · {detail.BountyEntries.Count} payouts"
            : "nothing measured";
    }

    private void _ApplyLoot(ActivityDetailDto detail, IReadOnlyDictionary<int, decimal> unitPrices)
    {
        LootCaptureRows.Clear();
        foreach (RunLootCaptureDto capture in detail.Runs.SelectMany(run => run.LootCaptures)
                     .OrderBy(capture => capture.CapturedAtUtc))
            LootCaptureRows.Add(new ActivityLootCaptureRowViewModel(capture,
                [.. capture.Entries.Select(entry => new ActivityLootLineViewModel(entry,
                    unitPrices.TryGetValue(entry.ItemTypeId, out decimal price) ? price : null))]));

        HasLootFigures = LootCaptureRows.Count > 0;
        // The totals are the summary's own, already built from this same lookup with the excluded captures left
        // out — recomputing them here is how a detail starts disagreeing with the row that led to it (ET-160).
        LootIskText = _IskOrNoPrice(detail.LootIskGained);
        ConsumedIskText = _IskOrNoPrice(detail.LootIskLost);
        NetIskText = _IskOrNoPrice(detail.LootIskNet);
        LootEmptyText = HasLootFigures
            ? null
            : "No loot capture was recorded for this activity — nothing was copied, so there is nothing to value.";

        int withoutPrice = LootCaptureRows.Where(capture => !capture.IsExcluded)
            .SelectMany(capture => capture.Lines).Count(line => !line.HasPrice);
        LinesWithoutPriceText = withoutPrice switch
        {
            0 => null,
            1 => "1 line has no price in the cache and counts towards nothing.",
            _ => $"{withoutPrice} lines have no price in the cache and count towards nothing."
        };

        Loot.HeaderSummary = HasLootFigures
            ? $"{NetIskText} · {LootCaptureRows.Count} captures · {LootCaptureRows.Count(row => row.IsExcluded)} excluded"
            : "nothing captured";
    }

    private void _ApplyEscalation(ActivityDetailDto detail)
    {
        RunParameterDto? escalation = detail.Parameters
            .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.Escalation);
        EscalationText = escalation?.TypedValue;
        EscalationObservedText = escalation is null
            ? null
            : $"read from the Agency at {escalation.ObservedAtUtc.ToLocalTime():HH:mm} on " +
              $"{escalation.ObservedAtUtc.ToLocalTime():d MMM}";
        EscalationEmptyText = escalation is null ? "No escalation has been registered for this activity." : null;
        Escalation.HeaderSummary = escalation?.TypedValue ?? "none registered";
    }

    /// <summary>
    /// Which sections this kind has, and one line for the ones it does not. ACTIVITY, ENEMIES and FLEET are on every
    /// kind — every run has an identity, a fight that never happened is still a measurement, and every activity has
    /// a crew even when that crew is one pilot. The other four are a judgment about the kind, overridden whenever
    /// there is data for them, because a table is not a reason to hide a stored row.
    /// </summary>
    private void _ApplySectionsPerKind(ActivityKind kind)
    {
        IsRewardsShown = kind == ActivityKind.Mission || RewardRows.Count > 0;
        IsBountyShown = kind is ActivityKind.Abyssal or ActivityKind.Site || HasBountyFigures;
        IsLootShown = kind is ActivityKind.Abyssal or ActivityKind.Site || LootCaptureRows.Count > 0;
        IsEscalationShown = kind == ActivityKind.Site || EscalationText is not null;

        string noun = _KindNoun(kind);
        List<string> absent = [];
        if (!IsRewardsShown)
            absent.Add($"no REWARDS — {noun} pays in what it drops, not in a reward agreed beforehand");
        if (!IsBountyShown)
            absent.Add($"no BOUNTY — {noun} has no rats whose bounty lands in your wallet");
        if (!IsLootShown)
            absent.Add($"no LOOT — {noun} leaves no wrecks to empty");
        if (!IsEscalationShown)
            absent.Add($"no ESCALATION — {noun} does not escalate");

        // Named rather than dropped because a section that is simply missing reads exactly like one that is empty,
        // and the two mean opposite things. The line says which sections and why; it does not explain itself to the
        // reader, who came here to see their run and not the reasoning behind the screen.
        AbsentSectionsText = absent.Count == 0
            ? null
            : $"Not shown for this kind of activity: {string.Join("; ", absent)}.";
    }

    private static bool _IsRewardParameter(RunParameterDto parameter) =>
        parameter.ParameterKey is not (RunParameterKey.Escalation or RunParameterKey.Smugglers
            or RunParameterKey.Civilians);

    /// <summary>"no price" and not "0 ISK": a figure nobody has must not look like a figure that came out at zero
    /// (ET-65 AC-5).</summary>
    private static string _IskOrNoPrice(decimal? isk) => isk is { } value ? $"{value:N2} ISK" : "no price";

    private static string _KindLabel(ActivityKind kind) => kind switch
    {
        ActivityKind.Abyssal => "Abyssal",
        ActivityKind.Site => "Combat Site",
        ActivityKind.Mission => "Mission",
        _ => kind.ToString()
    };

    // Default arm rather than a throw: ActivityKind is stored by value and only ever grows, and a kind this screen
    // has never heard of should read a little vaguer, not take the window down (AGENTS.md §2).
    private static string _KindNoun(ActivityKind kind) => kind switch
    {
        ActivityKind.Abyssal => "an abyssal pocket",
        ActivityKind.Site => "a site",
        ActivityKind.Mission => "a mission",
        _ => "this kind of activity"
    };
}
