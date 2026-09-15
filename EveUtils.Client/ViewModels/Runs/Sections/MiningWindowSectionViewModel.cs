using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// MINING in the run window: what each participant's own gamelog has mined so far, per ore (ET-229) — general for
/// any mining, not only a homefront's. Priced through the price source, Mutanite at its fixed NPC price
/// (<see cref="MiningValuation"/>). Contributes a share of TOTAL ISK — see <see cref="IskSource.Mining"/>.
///
/// <see cref="FleetMinedText"/> and <see cref="RemainingText"/> (ET-234) add what the rest of the fleet shares
/// (<c>RunShareUpdate</c>, ET-242's wire) on top of this window's own rows — the same "counted from what members
/// share" honesty <see cref="FleetWindowSectionViewModel.FleetBasisText"/> already states for loot and bounty.
///
/// <see cref="Groups"/> (ET-283, variant C of the mining-ledger mockups) is the same data grouped one row per
/// character: this window's own participants first, then whoever else shares mining (per-ore lines when their client
/// sends them, ET-234's old total-only shape otherwise), then an external member with nothing shared at all — a
/// "not shared" row, never counted, mirroring FLEET's own convention (ET-272).
///
/// Worked out again every tick, but shown by identity (ET-287): a row that says the same stays the object it is, and a
/// group only takes over what changed. Clearing and refilling both collections every second recreated every container,
/// share bar and tooltip under MINING once a second, for the whole run.
/// </summary>
public sealed class MiningWindowSectionViewModel : RunWindowSection
{
    private static readonly TimeSpan LiveRateWindow = TimeSpan.FromMinutes(5);

    // A boost still reads "boosting"/"boosted" this long after its last observed burst line — long enough to span a
    // couple of missed cycles on top of the ~60 s cadence measured on Abnoba Auscent's Orca (design/mining-ledger/v1).
    private static readonly TimeSpan BoostFreshness = TimeSpan.FromMinutes(3);

    // An ore the price source had no answer for is asked again this long after, not every tick (ET-287): its cache is
    // refreshed hourly, so asking every second only ever repeated the same miss.
    private static readonly TimeSpan PriceRetryInterval = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Dictionary<int, List<(DateTime AtUtc, string OreType, int Units)>> _recentYield = [];
    private readonly Dictionary<int, BoostState> _boost = [];
    private readonly GamelogClientService? _gamelog;

    // Ore prices barely move mid-run and there are only ever a handful of distinct ores on one run, so every priced
    // type id is kept for the life of the section rather than re-asked once it has an answer.
    private readonly Dictionary<int, double> _prices = new();
    private readonly HashSet<int> _priceAsked = [];

    // An ore's type never changes, so the SDE is asked once per ore name (ET-298): every tick prices every ore line
    // and every cycle of the live rate, and a query each time — on the UI thread — is what froze the window mid-run.
    // An ore the SDE does not know is remembered as null; nothing is remembered while the SDE is unavailable.
    private readonly Dictionary<string, OreType?> _oreTypes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _pricesAskedAtUtc;
    private bool _isPricing;

    private DateTime _nowUtc;

    public MiningWindowSectionViewModel(IRunWindowContext context) : base(context, RunSectionId.Mining, "MINING")
    {
        _gamelog = context.Services.GetService<GamelogClientService>();
        if (_gamelog is not null)
        {
            _gamelog.MiningObserved += _OnMiningObserved;
            _gamelog.MiningBoostObserved += _OnMiningBoost;
        }
    }

    public ObservableCollection<ActivityMiningRowViewModel> Rows { get; } = [];

    /// <summary>The same mining, grouped one row per character (ET-283) — what the view draws; <see cref="Rows"/>
    /// stays flat underneath it for <see cref="FactsFor"/> and <see cref="UnitsFor"/>, which never needed grouping.</summary>
    public ObservableCollection<MiningCharacterGroupViewModel> Groups { get; } = [];

    /// <summary>The whole fleet's mined units so far — this window's own rows plus whoever else shares (ET-234).
    /// Null with no fleet at all (ET-284: a solo run has nobody else's mining to count, so a line about "what
    /// members share" only confused Jithran's own 13 Sep run), and while there is nothing to say at all: no own
    /// mining and nobody sharing.</summary>
    public string? FleetMinedText { get; private set; }

    /// <summary>What is left of a Metaliminal Meteoroid's 5,000-unit asteroid (<see cref="IRunWindowContext.RunType"/>'s
    /// own <c>SiteMiningCapacityUnits</c>) — null for any site whose capacity is not a known figure, including an
    /// ordinary mining fleet and AAR (ET-234).</summary>
    public string? RemainingText { get; private set; }

    /// <summary>Whether <see cref="FleetMinedText"/> is worth its own line (ET-284) — false whenever
    /// <see cref="RemainingText"/> already says as much as part of naming what capacity is left, so a Metaliminal
    /// fleet never reads two short, overlapping lines under one MINING run.</summary>
    public bool ShowFleetMinedText { get; private set; }

    public override void Refresh(DateTime nowUtc)
    {
        _nowUtc = nowUtc;
        _SyncRows();
        _RefreshFleetTotals();
        _SyncGroups(nowUtc);
        _ = _RefreshPricesAsync();
    }

    public override void RefreshSummary()
    {
        decimal? total = _Total();
        HeaderSummary = Rows.Count == 0
            ? "no mining measured"
            : total is { } isk
                ? $"{IskFormat.Whole(isk)} — {Rows.Count} ore lines"
                : $"{Rows.Count} ore lines — no price yet";
    }

    protected override void OnContextChanged(string? propertyName)
    {
        if (propertyName is nameof(IRunWindowContext.GroupCode) or nameof(IRunWindowContext.RunId))
            lock (_gate)
            {
                _recentYield.Clear();
                _boost.Clear();
            }
    }

    public override void Dispose()
    {
        if (_gamelog is not null)
        {
            _gamelog.MiningObserved -= _OnMiningObserved;
            _gamelog.MiningBoostObserved -= _OnMiningBoost;
        }
        base.Dispose();
    }

    /// <summary>One participant's own mining, for the group ISK total (ET-256) — the same shape
    /// <c>ActivityWindowViewModel._ConsumableFacts</c> reads from CONSUMABLES.</summary>
    public (decimal? Value, bool Has) FactsFor(Guid runId)
    {
        ActivityMiningRowViewModel[] rows = [.. Rows.Where(row => row.RunId == runId)];
        if (rows.Length == 0)
            return (null, false);

        return rows.Any(row => row.Value is not null) ? (rows.Sum(row => row.Value.GetValueOrDefault()), true) : (null, true);
    }

    private decimal? _Total() => Rows.Any(row => row.Value is not null) ? Rows.Sum(row => row.Value.GetValueOrDefault()) : null;

    /// <summary>This run's own total units mined (crit included), for <see cref="MetricKind.MiningYield"/>'s
    /// producer — the same per-run sum <see cref="FactsFor"/> already keeps for ISK, in units instead.</summary>
    public int UnitsFor(Guid runId) => Rows.Where(row => row.RunId == runId).Sum(row => row.Units);

    /// <summary>
    /// The whole fleet's mining, own rows plus whoever else shares (ET-234) — never this window's own characters
    /// twice, the same de-duplication <see cref="LootWindowSectionViewModel"/> applies against
    /// <see cref="IRunWindowContext.Participants"/>.
    ///
    /// Crit is counted into <see cref="FleetMinedText"/> as real ore in the hold, but not subtracted from
    /// <see cref="RemainingText"/>'s capacity — a crit yield takes nothing extra from the asteroid
    /// (domain/homefronts.md §6.1). Treating it as consumed anyway is a deliberate, conservative simplification: it
    /// slightly under-reports what is left rather than over-promise it, and it spares the wire a third field for an
    /// effect measured at under 2% of mining lines.
    /// </summary>
    private void _RefreshFleetTotals()
    {
        int units = Rows.Sum(row => row.Units);
        int residue = Rows.Sum(row => row.ResidueUnits);
        bool hasShared = false;
        bool inFleet = Context.GroupCode is not null && Context.FleetId is not null;

        if (Context.GroupCode is { } groupCode && Context.FleetId is { } fleetId
            && Context.Services.GetService<FleetRunShares>() is { } shares)
        {
            HashSet<int> own = [.. Context.Participants.Select(participant => participant.CharacterId)];
            foreach ((int characterId, RunShareUpdate share) in shares.Of(groupCode))
            {
                if (share.FleetId != fleetId || !share.SharesMining || own.Contains(characterId))
                    continue;

                units += share.MinedUnits;
                residue += share.ResidueUnits;
                hasShared = true;
            }
        }

        string? fleetMined = !inFleet || (Rows.Count == 0 && !hasShared)
            ? null
            : $"fleet mined {IskFormat.Number(units)} units. Counted from what members share — a member sharing " +
              "nothing is missing from this total.";

        string? remaining = Context.RunType.SiteMiningCapacityUnits is { } capacity && fleetMined is not null
            ? $"~{IskFormat.Number(Math.Max(0, capacity - units - residue))} units remaining in the site. Only as " +
              "accurate as what the fleet shares."
            : null;

        bool showFleetMined = fleetMined is not null && remaining is null;

        // Raised only on a change (ET-287): every notification re-measures its line in the view.
        if (fleetMined != FleetMinedText)
        {
            FleetMinedText = fleetMined;
            OnPropertyChanged(nameof(FleetMinedText));
        }
        if (remaining != RemainingText)
        {
            RemainingText = remaining;
            OnPropertyChanged(nameof(RemainingText));
        }
        if (showFleetMined != ShowFleetMinedText)
        {
            ShowFleetMinedText = showFleetMined;
            OnPropertyChanged(nameof(ShowFleetMinedText));
        }
    }

    /// <summary>Worked out every tick from <see cref="IRunWindowContext.Participants"/>, which already carries each
    /// one's own <c>RunMiningEntry</c> rows (ET-229) — and shown by identity (ET-287): a row that reads the same as the
    /// one already shown keeps that object, so only a line that really moved is replaced.</summary>
    private void _SyncRows()
    {
        ISdeAccessor? sde = Context.Services.GetService<ISdeAccessor>();
        List<ActivityMiningRowViewModel> rows = [];
        foreach (RunParticipantViewModel participant in Context.Participants)
            foreach (RunMiningOreDto entry in participant.MiningEntries.OrderByDescending(e => e.Units))
            {
                (decimal? unitPrice, bool isFixedPrice) = _PriceOf(sde, entry.OreType);
                rows.Add(new ActivityMiningRowViewModel(
                    participant.RunId, participant.CharacterId, entry.OreType, entry.Units, entry.CriticalUnits,
                    entry.ResidueUnits, unitPrice is { } price ? price * entry.Units : null, isFixedPrice,
                    _ => participant.CharacterName));
            }

        Rows.ReconcileTo([.. rows.Select(row => Rows.FirstOrDefault(shown => shown.ShowsSameAs(row)) ?? row)]);
        RefreshSummary();
    }

    /// <summary>Prices every distinct ore not yet priced, one appraisal call for the whole set — once per ore rather
    /// than once per tick (ET-287): an ore already asked is asked again only after <see cref="PriceRetryInterval"/>,
    /// Mutanite is never asked at all (its price is <see cref="MiningValuation"/>'s fixed NPC figure, whatever the
    /// market says), a call still under way is never started a second time, and the store is read off the UI thread.</summary>
    private async Task _RefreshPricesAsync()
    {
        if (_isPricing
            || Context.Services.GetService<ISdeAccessor>() is not { IsAvailable: true } sde
            || Context.Services.GetService<IAppraisalProvider>() is not { } appraisal)
            return;

        if (_pricesAskedAtUtc is { } askedAt && (_nowUtc - askedAt >= PriceRetryInterval || _nowUtc < askedAt))
            _priceAsked.Clear();

        int[] typeIds = [.. Context.Participants.SelectMany(p => p.MiningEntries).Select(e => e.OreType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ore => _OreTypeOf(sde, ore))
            .OfType<OreType>()
            .Where(ore => !ore.IsMutanite)
            .Select(ore => ore.TypeId)
            .Where(id => !_prices.ContainsKey(id) && !_priceAsked.Contains(id))
            .Distinct()];
        if (typeIds.Length == 0)
            return;

        _isPricing = true;
        _priceAsked.UnionWith(typeIds);
        _pricesAskedAtUtc = _nowUtc;
        try
        {
            Result<AppraisalOutcome> valued = await Task.Run(() => appraisal.AppraiseAsync(
                [.. typeIds.Select(id => new AppraisalLine(id, string.Empty, 1))]));
            if (!valued.IsSuccess)
                return;

            bool isPriced = false;
            foreach (AppraisalRow row in valued.Value!.Rows)
                if (row.Price?.Estimate is { } estimate)
                {
                    _prices[row.Line.TypeId] = estimate;
                    isPriced = true;
                }

            if (!isPriced)
                return;

            _SyncRows();
            _SyncGroups(_nowUtc);
        }
        finally
        {
            _isPricing = false;
        }
    }

    // The gamelog watcher's pump thread (or, on RESUME, ET-258's catch-up read) writes these; this window's own
    // tick reads them, the same split HomefrontWindowSectionViewModel's _heard/_paleShadow already use.
    private void _OnMiningObserved(int characterId, string oreType, int units, DateTime atUtc)
    {
        lock (_gate)
        {
            if (!_recentYield.TryGetValue(characterId, out List<(DateTime AtUtc, string OreType, int Units)>? events))
                _recentYield[characterId] = events = [];
            events.Add((atUtc, oreType, units));
            events.RemoveAll(observed => atUtc - observed.AtUtc > LiveRateWindow);
        }
    }

    private void _OnMiningBoost(int characterId, string module, int reachCount, DateTime atUtc)
    {
        lock (_gate)
            _boost[characterId] = _boost.TryGetValue(characterId, out BoostState? existing)
                ? existing with { Module = module, BurstCount = existing.BurstCount + 1, LastAtUtc = atUtc, LastReachCount = reachCount }
                : new BoostState(module, 1, atUtc, atUtc, reachCount);
    }

    private void _SyncGroups(DateTime nowUtc)
    {
        ISdeAccessor? sde = Context.Services.GetService<ISdeAccessor>();
        FleetRunShares? shares = Context.Services.GetService<FleetRunShares>();

        List<CharacterBuild> builds = [];
        HashSet<int> ownIds = [];
        foreach (IGrouping<int, RunParticipantViewModel> character in Context.Participants.GroupBy(p => p.CharacterId))
        {
            ownIds.Add(character.Key);
            Dictionary<string, (int Units, int Crit, int Residue)> ores = [];
            foreach (RunMiningOreDto entry in character.SelectMany(p => p.MiningEntries))
                _Add(ores, entry.OreType, entry.Units, entry.CriticalUnits, entry.ResidueUnits);
            builds.Add(new CharacterBuild(character.Key, character.First().CharacterName, IsLocal: true, ores));
        }

        if (Context.GroupCode is { } groupCode && Context.FleetId is { } fleetId && shares is not null)
        {
            HashSet<int> sharedIds = [];
            foreach ((int characterId, RunShareUpdate share) in shares.Of(groupCode))
            {
                if (share.FleetId != fleetId || !share.SharesMining || ownIds.Contains(characterId))
                    continue;

                sharedIds.Add(characterId);
                // Shares mining but has nothing yet (0 captures, an empty per-ore list) — nothing to draw a row for
                // at all, own or fallback; not the same as an older client sending the total-only shape below.
                if (share.MinedUnits == 0 && share.Mining.Count == 0)
                    continue;

                string name = Context.FleetMembers.FirstOrDefault(member => member.CharacterId == characterId)?.Name
                              ?? $"character {characterId}";
                if (share.Mining.Count > 0)
                {
                    Dictionary<string, (int Units, int Crit, int Residue)> ores = [];
                    foreach (RunShareMiningLine line in share.Mining)
                        _Add(ores, line.OreType, line.Units, line.CriticalUnits, line.ResidueUnits);
                    builds.Add(new CharacterBuild(characterId, name, IsLocal: false, ores));
                }
                else
                {
                    builds.Add(new CharacterBuild(characterId, name, IsLocal: false, [],
                        IsFallback: true, FallbackUnits: share.MinedUnits));
                }
            }

            foreach (ActivityFleetMemberViewModel member in Context.FleetMembers)
                if (!ownIds.Contains(member.CharacterId) && !sharedIds.Contains(member.CharacterId))
                    builds.Add(new CharacterBuild(member.CharacterId, member.Name, IsLocal: false, [], IsNotShared: true));
        }

        // Per-ore fleet totals across every counted character (own + a shared member whose client sent per-ore
        // lines) — what a fleet scenario's bar reads a character's own units against. A fallback or not-shared row
        // carries no ore split, so it never enters this and never enters the fleet-scenario decision either.
        CharacterBuild[] counted = [.. builds.Where(build => build is { IsFallback: false, IsNotShared: false })];
        Dictionary<string, int> fleetOreUnits = [];
        foreach (CharacterBuild build in counted)
            foreach ((string ore, (int units, int _, int _)) in build.Ores)
                fleetOreUnits[ore] = fleetOreUnits.GetValueOrDefault(ore) + units;
        bool isFleetScenario = counted.Length > 1;

        List<(MiningCharacterGroupViewModel Group, bool IsNotShared, decimal Isk)> built =
            [.. builds.Select(build => _ToGroup(build, sde, fleetOreUnits, isFleetScenario, nowUtc))];

        List<MiningCharacterGroupViewModel> shown = [];
        foreach (MiningCharacterGroupViewModel fresh in built
                     .OrderByDescending(entry => entry.Group.IsLocal)
                     .ThenBy(entry => entry.IsNotShared)
                     .ThenByDescending(entry => entry.Isk)
                     .Select(entry => entry.Group))
        {
            if (Groups.FirstOrDefault(group => group.CanTakeOver(fresh)) is { } kept && !shown.Contains(kept))
            {
                kept.TakeOver(fresh);
                shown.Add(kept);
            }
            else
                shown.Add(fresh);
        }

        Groups.ReconcileTo(shown);
    }

    private (MiningCharacterGroupViewModel Group, bool IsNotShared, decimal Isk) _ToGroup(
        CharacterBuild build, ISdeAccessor? sde, IReadOnlyDictionary<string, int> fleetOreUnits, bool isFleetScenario,
        DateTime nowUtc)
    {
        if (build.IsNotShared)
            return (new MiningCharacterGroupViewModel(build.CharacterId, build.Name, false, null,
                string.Empty, null, null, null, null, [], fallbackText: null, isNotShared: true), true, 0m);

        if (build.IsFallback)
        {
            string fallback = $"all ores · {IskFormat.Number(build.FallbackUnits)} units";
            return (new MiningCharacterGroupViewModel(build.CharacterId, build.Name, false, null,
                string.Empty, null, null, null, null, [], fallbackText: fallback), false, 0m);
        }

        List<(string Ore, int Units, int Crit, int Residue, decimal? Isk, decimal? UnitPrice, bool IsFixedPrice)> lines = [];
        foreach ((string ore, (int units, int crit, int residue)) in build.Ores)
        {
            (decimal? unitPrice, bool isFixedPrice) = _PriceOf(sde, ore);
            lines.Add((ore, units, crit, residue, unitPrice is { } price ? price * units : null, unitPrice, isFixedPrice));
        }

        decimal? isk = lines.Any(line => line.Isk is not null) ? lines.Sum(line => line.Isk.GetValueOrDefault()) : null;
        int totalResidue = lines.Sum(line => line.Residue);
        decimal residueIsk = lines.Sum(line => (line.UnitPrice ?? 0m) * line.Residue);

        List<ActivityMiningRowViewModel> oreRows = [];
        foreach ((string ore, int units, int crit, int residue, decimal? lineIsk, decimal? _, bool isFixedPrice) in
                 lines.OrderByDescending(line => line.Units))
        {
            double? shareFraction = null;
            string? shareTooltip = null;
            if (isFleetScenario)
            {
                int fleetUnits = fleetOreUnits.GetValueOrDefault(ore);
                shareFraction = fleetUnits > 0 ? (double)units / fleetUnits : 0;
                shareTooltip = $"{build.Name} · {ore} — {IskFormat.Number(units)} of the fleet's " +
                                $"{IskFormat.Number(fleetUnits)} units";
            }
            else if (isk is { } total && total > 0 && lineIsk is not null)
            {
                shareFraction = (double)(lineIsk.Value / total);
                shareTooltip = $"{ore} — part of {build.Name}'s own ISK mix";
            }

            oreRows.Add(new ActivityMiningRowViewModel(Guid.Empty, build.CharacterId, ore, units, crit, residue,
                lineIsk, isFixedPrice, _ => build.Name, shareFraction, shareTooltip));
        }

        string? iskTooltip = MiningCharacterGroupViewModel.IskTooltipFor(
            lines.Any(line => line.IsFixedPrice), totalResidue, residueIsk);

        // A character of this window's own who has mined nothing reads "no mining yet" instead of a rate (ET-288).
        (string RateText, string? RateTooltip) rate = build.IsLocal && lines.Count > 0
            ? _LiveRateFor(build.CharacterId, sde, nowUtc)
            : (string.Empty, null);
        (string? Glyph, string? Tooltip) boost = build.IsLocal ? _BoostFor(build.CharacterId, nowUtc) : (null, null);

        return (new MiningCharacterGroupViewModel(build.CharacterId, build.Name, build.IsLocal, isk,
            rate.RateText, rate.RateTooltip, iskTooltip, boost.Glyph, boost.Tooltip, oreRows), false,
            isk ?? 0m);
    }

    /// <summary>ISK/h "now" (ET-283): the yield of the last 5 minutes, times 12 — live only, from the gamelog event
    /// stream this window already receives (<see cref="_OnMiningObserved"/>); gone once the run is saved, the same
    /// as any other live rate in this app.</summary>
    private (string RateText, string? RateTooltip) _LiveRateFor(int characterId, ISdeAccessor? sde, DateTime nowUtc)
    {
        // Only the adding up happens under the lock the gamelog pump writes through (ET-298); pricing waits until the
        // pump is free to carry on.
        Dictionary<string, int> unitsPerOre = new(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            if (_recentYield.TryGetValue(characterId, out List<(DateTime AtUtc, string OreType, int Units)>? events))
                foreach ((DateTime at, string ore, int units) in events)
                    if (nowUtc - at <= LiveRateWindow)
                        unitsPerOre[ore] = unitsPerOre.GetValueOrDefault(ore) + units;

        decimal sum = 0m;
        bool any = false;
        foreach ((string ore, int units) in unitsPerOre)
            if (_PriceOf(sde, ore).UnitPrice is { } price)
            {
                sum += price * units;
                any = true;
            }

        return any
            ? ($"{IskFormat.Compact(sum * 12)} ISK/h now", "ISK/h = the yield of the last 5 minutes, times 12.")
            : ("— ISK/h now", null);
    }

    /// <summary>The unit price and whether it is Mutanite's fixed NPC one, for <see cref="_SyncRows"/>,
    /// <see cref="_ToGroup"/> and <see cref="_LiveRateFor"/> alike.</summary>
    private (decimal? UnitPrice, bool IsFixedPrice) _PriceOf(ISdeAccessor? sde, string oreType) =>
        _OreTypeOf(sde, oreType) is { } ore
            ? (MiningValuation.UnitPrice(ore.TypeId, ore.IsMutanite, _prices), ore.IsMutanite)
            : (null, false);

    private OreType? _OreTypeOf(ISdeAccessor? sde, string oreType)
    {
        if (_oreTypes.TryGetValue(oreType, out OreType? known))
            return known;
        if (sde is not { IsAvailable: true })
            return null;

        OreType? resolved = sde.TryGetTypeId(oreType, out int typeId)
            ? new OreType(typeId, MiningValuation.IsMutanite(sde, typeId))
            : null;
        _oreTypes[oreType] = resolved;
        return resolved;
    }

    /// <summary>▲▲ for the booster (this character's own gamelog wrote the burst lines), ▲ for another local
    /// character while a local booster's burst is still fresh — inferred, since a receiver's own log never says
    /// anything about a burst landing on them, even on this same PC. Null for anyone else: never "not boosted"
    /// (ET-283). A booster or receiver on another PC is out of reach here — nothing about a burst crosses the fleet
    /// wire yet, so a fleet mate elsewhere never gets a glyph from this window.</summary>
    private (string? Glyph, string? Tooltip) _BoostFor(int characterId, DateTime nowUtc)
    {
        BoostState? own;
        lock (_gate)
            own = _boost.GetValueOrDefault(characterId);
        if (own is { } booster && nowUtc - booster.LastAtUtc <= BoostFreshness)
            return ("▲▲", $"Boosting · {booster.BurstCount}× {booster.Module}, last reached {booster.LastReachCount} " +
                           $"fleet member(s) at {booster.LastAtUtc:HH:mm:ss}. Charge not known.");

        KeyValuePair<int, BoostState> other;
        lock (_gate)
            other = _boost.FirstOrDefault(candidate =>
                candidate.Key != characterId && nowUtc - candidate.Value.LastAtUtc <= BoostFreshness);
        if (other.Value is null)
            return (null, null);

        string boosterName = Context.Participants.FirstOrDefault(participant => participant.CharacterId == other.Key)
            ?.CharacterName ?? $"character {other.Key}";
        return ("▲", $"Boosted (inferred) — {boosterName}'s bursts since {other.Value.SinceUtc:HH:mm:ss}, last " +
                     $"reached {other.Value.LastReachCount} fleet member(s). Your own log says nothing when a burst " +
                     $"lands on you; this is read from {boosterName}'s log only.");
    }

    private static void _Add(Dictionary<string, (int Units, int Crit, int Residue)> ores, string oreType, int units,
        int crit, int residue)
    {
        (int Units, int Crit, int Residue) existing = ores.GetValueOrDefault(oreType);
        ores[oreType] = (existing.Units + units, existing.Crit + crit, existing.Residue + residue);
    }

    private sealed record CharacterBuild(
        int CharacterId, string Name, bool IsLocal, Dictionary<string, (int Units, int Crit, int Residue)> Ores,
        bool IsFallback = false, int FallbackUnits = 0, bool IsNotShared = false);

    private sealed record BoostState(string Module, int BurstCount, DateTime SinceUtc, DateTime LastAtUtc, int LastReachCount);

    private sealed record OreType(int TypeId, bool IsMutanite);
}
