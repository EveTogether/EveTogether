using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// What a run window offers the fleet of the shared run it flies (ET-242): two toggles on its face — loot and bounty,
/// for this run only — and the loot itself, sent as each own character's <see cref="RunShareUpdate"/>.
///
/// Whether a figure is shared is not decided here but by <see cref="MetricShareSnapshot"/>, the same answer the metric
/// publisher gates the ISK figures on: the run's own choice, then the fleet's override, and on a shared run without
/// either — shared. This window's part is to put its characters on the run (<see cref="SharedFleetRuns"/>) for exactly
/// as long as it shows the toggles that say so, and to write the run's choice when one is pressed.
///
/// Only a fleet on a server: a client-only fleet's figures never leave this machine, so there is nothing to offer and
/// no toggle to show.
/// </summary>
public sealed partial class RunFleetSharingViewModel(IServiceProvider services) : ObservableObject
{
    /// <summary>Captures come in bursts — a pilot copies three cans in as many seconds — so a change waits this long for
    /// the rest of its burst and goes out once, the rule ET-222 set for the screens applied to the wire.</summary>
    public static readonly TimeSpan BundleWindow = TimeSpan.FromSeconds(2);

    /// <summary>Sent again this often while anything is shared, for a member whose client connected after the last
    /// change; a pilot who stops sharing is told at once instead.</summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(30);

    // A second read of the settings within the same second tells nothing the first did not; a pressed toggle reads again.
    private static readonly TimeSpan SettingsReadInterval = TimeSpan.FromSeconds(1);

    private static readonly MetricKind[] RunScopedKinds = [MetricKind.Loot, MetricKind.Bounty];

    private readonly Dictionary<int, SentShare> _sent = [];
    private readonly Dictionary<int, DateTime> _changedSince = [];
    private readonly HashSet<string> _runsTaken = new(StringComparer.Ordinal);
    private (long FleetId, string GroupCode)? _run;
    private int[] _characters = [];
    private SyncFacts? _facts;
    private MetricShareSnapshot? _share;
    private DateTime _shareReadAtUtc;
    private bool _isSyncing;
    private bool _isSyncOwed;

    /// <summary>This run is shared with a fleet on a server, and one of this client's own characters flies it.</summary>
    [ObservableProperty] private bool _isShown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LootToggleText))]
    [NotifyPropertyChangedFor(nameof(SharingText))]
    private bool _isSharingLoot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BountyToggleText))]
    [NotifyPropertyChangedFor(nameof(SharingText))]
    private bool _isSharingBounty;

    // The state in words on the button itself, not in its colour alone: an Amarr accent and the resting ink are close.
    public string LootToggleText => IsSharingLoot ? "loot: shared" : "loot: not shared";

    public string BountyToggleText => IsSharingBounty ? "bounty: shared" : "bounty: not shared";

    public string SharingText => (IsSharingLoot, IsSharingBounty) switch
    {
        (true, true) => "Your loot and bounty on this run go to the fleet. Click one to stop sharing it.",
        (true, false) => "Your loot on this run goes to the fleet; your bounty does not.",
        (false, true) => "Your bounty on this run goes to the fleet; your loot does not.",
        _ => "Nothing of your loot or bounty on this run goes to the fleet."
    };

    /// <summary>
    /// One tick of the window's clock. <paramref name="runCharacters"/> are the characters with a run in this group as
    /// the window knows them — its own and every participant — of which only this client's own characters in the
    /// fleet count; a group mate's run published to this client is theirs to share.
    /// </summary>
    public Task SyncAsync(DateTime nowUtc, long? fleetId, string? groupCode, bool isRunOpen,
        IReadOnlyCollection<int> runCharacters, ActivityLootViewModel? loot)
    {
        _facts = new SyncFacts(nowUtc, fleetId, groupCode, isRunOpen, runCharacters, loot);
        return _SyncOrOweAsync(readSettings: false);
    }

    /// <summary>
    /// The run is over for this window — saved, thrown away, or the window closing. Its characters come off the run at
    /// once, so the next publish tick is back on the fleet and global choices. <paramref name="forget"/> also removes
    /// the run's own choices, which nothing reads once the run is committed.
    /// </summary>
    public void Release(bool forget)
    {
        _TakeCharacters(null, []);
        _sent.Clear();
        _changedSince.Clear();
        _facts = null;
        IsShown = false;

        if (!forget || _runsTaken.Count == 0)
            return;

        string[] groupCodes = [.. _runsTaken];
        _runsTaken.Clear();
        _ = _ForgetAsync(groupCodes);
    }

    [RelayCommand]
    private Task ToggleLootAsync() => _ChooseAsync(MetricKind.Loot, !IsSharingLoot);

    [RelayCommand]
    private Task ToggleBountyAsync() => _ChooseAsync(MetricKind.Bounty, !IsSharingBounty);

    private async Task _ChooseAsync(MetricKind kind, bool isShared)
    {
        if (_run is not { } run || services.GetService<CqrsDispatcher>() is null)
            return;

        using (IServiceScope scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Send(new SetSettingCommand(MetricShareSnapshot.RunKeyFor(run.GroupCode, kind), isShared ? "true" : "false"));

        // On the clock's last tick: switching off goes out at once regardless, and switching on waits out the same
        // bundle window any other change does.
        await _SyncOrOweAsync(readSettings: true);
    }

    // One sync at a time: the clock and a pressed toggle can both ask, and a second read racing the first could leave
    // the older answer standing. A request that arrives mid-sync runs once more after it, on the newest facts.
    private async Task _SyncOrOweAsync(bool readSettings)
    {
        if (_isSyncing)
        {
            _isSyncOwed = true;
            return;
        }

        _isSyncing = true;
        try
        {
            do
            {
                _isSyncOwed = false;
                await _SyncOnceAsync(readSettings);
                readSettings = true;
            }
            while (_isSyncOwed);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task _SyncOnceAsync(bool readSettings)
    {
        if (_facts is not { IsRunOpen: true, FleetId: { } fleetId, GroupCode: { } groupCode } facts)
        {
            _TakeCharacters(null, []);
            IsShown = false;
            return;
        }

        DateTime nowUtc = facts.NowUtc;

        int[] characters = [.. (services.GetService<IFleetParticipation>()?.Current ?? [])
            .Where(participant => participant.FleetId == fleetId && !participant.ClientOnly
                                  && facts.RunCharacters.Contains(participant.CharacterId))
            .Select(participant => participant.CharacterId)
            .Distinct()];
        _TakeCharacters((fleetId, groupCode), characters);
        IsShown = characters.Length > 0;
        if (!IsShown || services.GetService<IMetricShareSettings>() is not { } settings)
            return;

        TimeSpan age = nowUtc - _shareReadAtUtc;
        if (readSettings || _share is null || age < TimeSpan.Zero || age >= SettingsReadInterval)
        {
            _share = await settings.LoadAsync();
            _shareReadAtUtc = nowUtc;
        }

        MetricShareSnapshot share = _share;
        IsSharingLoot = characters.Any(character => share.IsShared(fleetId, character, MetricKind.Loot));
        IsSharingBounty = characters.Any(character => share.IsShared(fleetId, character, MetricKind.Bounty));

        foreach (int character in characters)
            await _OfferAsync(nowUtc, fleetId, groupCode, character, share, facts.Loot);
    }

    private async Task _OfferAsync(DateTime nowUtc, long fleetId, string groupCode, int character,
        MetricShareSnapshot share, ActivityLootViewModel? loot)
    {
        bool sharesLoot = share.IsShared(fleetId, character, MetricKind.Loot);
        bool sharesBounty = share.IsShared(fleetId, character, MetricKind.Bounty);
        ActivityLootCharacterViewModel[] blocks = sharesLoot && loot is not null
            ? [.. loot.Characters.Where(block => block.CharacterId == character)]
            : [];
        // What counts on the run, one line per kind — the rows the pilot's own LOOT section lists above its line of
        // exclusions, so the others see the pilot's own answer and never the captures it was worked out from.
        RunShareLootLine[] lines = [.. blocks
            .SelectMany(block => block.Loot.ItemRows)
            .Where(row => !row.IsExcluded)
            .GroupBy(row => (row.ItemTypeId, Kind: row.IsLost ? LootKind.Lost : LootKind.Gained))
            .Select(group => new RunShareLootLine(group.Key.ItemTypeId, group.Sum(row => row.Quantity ?? 1), group.Key.Kind))
            .OrderBy(line => line.TypeId)
            .ThenBy(line => line.Kind)];
        int captures = blocks.Sum(block => block.Loot.Captures.Count(capture => !capture.IsExcluded));
        SentShare current = new(sharesLoot, sharesBounty, captures, lines, nowUtc);

        SentShare? sent = _sent.GetValueOrDefault(character);
        // Taking something back is never held for a burst: from this moment the others see nothing more of it.
        bool isWithdrawn = sent is not null && (sent.SharesLoot && !sharesLoot || sent.SharesBounty && !sharesBounty);
        bool isChanged = sent is null ? sharesLoot || sharesBounty : !sent.SaysTheSameAs(current);
        if (!isChanged)
            _changedSince.Remove(character);

        bool isDue = isWithdrawn || (isChanged ? _HasSettled(character, nowUtc) : _IsResendDue(sent, current, nowUtc));
        if (!isDue || services.GetService<IEventBus>() is not { } eventBus)
            return;

        _sent[character] = current;
        _changedSince.Remove(character);
        await eventBus.PublishAsync(new FleetRunShareEvent(new RunShareUpdate(
                fleetId, groupCode, new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                sharesLoot, sharesBounty, captures, lines), character),
            EventTarget.Remote);
    }

    // A change waits out its burst, counted from the first tick that saw it.
    private bool _HasSettled(int character, DateTime nowUtc)
    {
        if (!_changedSince.TryGetValue(character, out DateTime since))
            _changedSince[character] = since = nowUtc;
        return nowUtc - since >= BundleWindow;
    }

    private static bool _IsResendDue(SentShare? sent, SentShare current, DateTime nowUtc) =>
        sent is not null && (current.SharesLoot || current.SharesBounty) && nowUtc - sent.SentAtUtc >= ResendInterval;

    /// <summary>Puts exactly these characters on the run in <see cref="SharedFleetRuns"/>, and takes off whoever this
    /// window had put there before and no longer flies it.</summary>
    private void _TakeCharacters((long FleetId, string GroupCode)? run, int[] characters)
    {
        SharedFleetRuns? runs = services.GetService<SharedFleetRuns>();
        if (_run is { } held)
            foreach (int character in _characters.Where(character => run != held || !characters.Contains(character)))
                runs?.Remove(held.FleetId, character, held.GroupCode);

        if (run != _run)
        {
            _sent.Clear();
            _changedSince.Clear();
        }

        if (run is { } taken)
        {
            foreach (int character in characters)
                runs?.Set(taken.FleetId, character, taken.GroupCode);
            if (characters.Length > 0)
                _runsTaken.Add(taken.GroupCode);
        }

        _run = run;
        _characters = characters;
    }

    private async Task _ForgetAsync(IReadOnlyList<string> groupCodes)
    {
        if (services.GetService<CqrsDispatcher>() is null)
            return;

        using IServiceScope scope = services.CreateScope();
        CqrsDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        foreach (string groupCode in groupCodes)
        foreach (MetricKind kind in RunScopedKinds)
            await dispatcher.Send(new DeleteSettingCommand(MetricShareSnapshot.RunKeyFor(groupCode, kind)));
    }

    private sealed record SyncFacts(
        DateTime NowUtc,
        long? FleetId, string? GroupCode, bool IsRunOpen, IReadOnlyCollection<int> RunCharacters, ActivityLootViewModel? Loot);

    private sealed record SentShare(bool SharesLoot, bool SharesBounty, int CaptureCount, RunShareLootLine[] Lines, DateTime SentAtUtc)
    {
        public bool SaysTheSameAs(SentShare other) =>
            SharesLoot == other.SharesLoot && SharesBounty == other.SharesBounty && CaptureCount == other.CaptureCount
            && Lines.SequenceEqual(other.Lines);
    }
}
