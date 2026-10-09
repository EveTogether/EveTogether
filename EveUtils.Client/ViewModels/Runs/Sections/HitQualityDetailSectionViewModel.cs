using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// HIT QUALITY on the detail screen (ET-474): per target and own weapon what the pilot dealt (OUT), and per source and
/// weapon what the enemy did (IN), as the count of each hit word with min, average, max and total, read from the hit
/// tallies SAVE kept (ET-467). Turrets and drones carry the same application the live meter gives; a missile never
/// does, its word is always "Hits". A run without tallies says why in one line.
/// </summary>
public sealed partial class HitQualityDetailSectionViewModel : RunDetailSection
{
    private static readonly HitQuality[] Words =
        [HitQuality.Wrecks, HitQuality.Smashes, HitQuality.Penetrates, HitQuality.Hits, HitQuality.Glances, HitQuality.Grazes, HitQuality.Misses];

    internal const string NotRecordedText =
        "No hit quality was recorded for this run: it was saved before EVE Together kept the game log's hit quality with the run.";

    internal const string NoCombatText = "No combat in this run: no shot was logged either way, so there is no hit quality.";

    internal const string MissileLabel = "n/a: see live application";

    internal const string MissileTip =
        "A missile always logs \"Hits\", so its hit words say nothing about how well it lands. See the live application meter, which reads each volley against the target.";

    private readonly CombatTelemetryChoice _choice;
    private readonly ISdeAccessor? _sde;

    public HitQualityDetailSectionViewModel(RunDetailSectionServices services) : base(RunSectionId.HitQuality, "HIT QUALITY")
    {
        _choice = services.Combat ?? new CombatTelemetryChoice(services.Dispatcher, services.OwnCharacterIds);
        _sde = services.Sde;
        _choice.PropertyChanged += _OnChoiceChanged;
        HeaderSummary = "no hit quality recorded";
        EmptyText = NotRecordedText;
    }

    public ObservableCollection<HitQualityRowViewModel> OutRows { get; } = [];

    public ObservableCollection<HitQualityRowViewModel> InRows { get; } = [];

    [ObservableProperty] private string? _emptyText;

    [ObservableProperty] private string _outSummary = string.Empty;

    [ObservableProperty] private string _inSummary = string.Empty;

    public bool HasOut => OutRows.Count > 0;

    public bool HasIn => InRows.Count > 0;

    public override bool HasContent => OutRows.Count + InRows.Count > 0;

    public override void Apply(RunDetailSectionInput input)
    {
    }

    // COMBAT loads the shared choice; this section only reads what it shows.
    public override Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private void _OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CombatTelemetryChoice.Shown))
        {
            return;
        }

        OutRows.Clear();
        InRows.Clear();
        IReadOnlyList<RunHitTallyDto> tallies = _choice.Shown?.HitTallies ?? [];
        if (tallies.Count == 0)
        {
            bool combat = _choice.Shown is not null && !_choice.FromStoredHits;
            EmptyText = combat ? NoCombatText : NotRecordedText;
            HeaderSummary = combat ? "no combat" : "no hit quality recorded";
            _Raise();
            return;
        }

        EmptyText = null;
        RunHitTallyDto[] outgoing = [.. tallies.Where(tally => tally.Direction is DamageDirection.Outgoing)];
        RunHitTallyDto[] incoming = [.. tallies.Where(tally => tally.Direction is DamageDirection.Incoming)];
        _Fill(OutRows, outgoing, true);
        _Fill(InRows, incoming, false);
        OutSummary = _BlockSummary(outgoing, false);
        InSummary = _BlockSummary(incoming, true);
        HeaderSummary = _Header(outgoing);
        _Raise();
    }

    private void _Raise()
    {
        OnPropertyChanged(nameof(HasOut));
        OnPropertyChanged(nameof(HasIn));
        OnPropertyChanged(nameof(HasContent));
    }

    private void _Fill(ObservableCollection<HitQualityRowViewModel> rows, RunHitTallyDto[] tallies, bool outgoing)
    {
        foreach (IGrouping<(string Counterparty, string? Weapon), RunHitTallyDto> group in tallies
                     .GroupBy(tally => (tally.Counterparty, tally.Weapon))
                     .OrderByDescending(group => group.Sum(tally => tally.Sum))
                     .ThenByDescending(group => group.Sum(tally => tally.Count))
                     .ThenBy(group => group.Key.Counterparty, StringComparer.Ordinal))
        {
            rows.Add(_Row(group.Key.Counterparty, group.Key.Weapon, group.ToArray(), outgoing));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].IsAlternate = i % 2 == 1;
        }
    }

    private HitQualityRowViewModel _Row(string counterparty, string? weapon, RunHitTallyDto[] group, bool outgoing)
    {
        int[] counts = [.. Words.Select(word => group.Where(tally => tally.Quality == word).Sum(tally => tally.Count))];
        RunHitTallyDto[] landed = [.. group.Where(tally => tally.Quality is not HitQuality.Misses && tally.Count > 0)];
        int hits = landed.Sum(tally => tally.Count);
        long total = landed.Sum(tally => tally.Sum);
        (string text, string? tip, bool sweet, bool adjust, bool unjudged) = outgoing
            ? _ApplicationOf(weapon, group)
            : (string.Empty, null, false, false, false);
        return new HitQualityRowViewModel(counterparty, weapon ?? "—", weapon is not null, _Number(counts.Sum()),
            [.. counts.Select(count => count > 0 ? _Number(count) : "·")],
            hits > 0 ? _Number(landed.Min(tally => tally.Min)) : "—",
            hits > 0 ? _Number((long)Math.Round((double)total / hits)) : "—",
            hits > 0 ? _Number(landed.Max(tally => tally.Max)) : "—",
            hits > 0 ? _Number(total) : "—",
            text, tip, sweet, adjust, unjudged);
    }

    private (string Text, string? Tip, bool Sweet, bool Adjust, bool Unjudged) _ApplicationOf(string? weapon, RunHitTallyDto[] group)
    {
        ApplicationSummary application = _Application(weapon, group);
        return application.Verdict switch
        {
            ApplicationVerdict.NotMeasurable => (MissileLabel, MissileTip, false, false, true),
            ApplicationVerdict.NotEnoughShots => ("—", $"Fewer than {WeaponApplicationTracker.MinShots} shots: too few to judge.", false, false, true),
            ApplicationVerdict.SweetSpot => ($"◆ SWEET SPOT · {_Percent(application)}", null, true, false, false),
            ApplicationVerdict.Adjust => ($"▲ ADJUST · {_Percent(application)}", null, false, true, false),
            _ => ($"● OK · {_Percent(application)}", null, false, false, false)
        };
    }

    private ApplicationSummary _Application(string? weapon, IEnumerable<RunHitTallyDto> group)
    {
        WeaponClass weaponClass = weapon is null ? WeaponClass.Unknown : WeaponClassifier.Classify(_sde, weapon);
        return WeaponApplicationTracker.ApplicationOf(weaponClass,
            [.. group.Where(tally => tally.Count > 0).GroupBy(tally => tally.Quality).Select(g => (g.Key, g.Sum(tally => tally.Count)))]);
    }

    // n targets · m weapons · the weapon that did the most damage and its label, as the live chip words it.
    private string _Header(RunHitTallyDto[] outgoing)
    {
        if (outgoing.Length == 0)
        {
            return "no shot dealt";
        }

        int targets = outgoing.Select(tally => tally.Counterparty).Distinct().Count();
        string[] weapons = [.. outgoing.Where(tally => tally.Weapon is not null).Select(tally => tally.Weapon!).Distinct()];
        string summary = $"{targets} {(targets == 1 ? "target" : "targets")} · {weapons.Length} {(weapons.Length == 1 ? "weapon" : "weapons")}";
        if (weapons.Length == 0)
        {
            return summary;
        }

        string main = weapons.OrderByDescending(weapon => outgoing.Where(tally => tally.Weapon == weapon).Sum(tally => tally.Sum)).First();
        ApplicationSummary application = _Application(main, outgoing.Where(tally => tally.Weapon == main));
        string label = application.Verdict switch
        {
            ApplicationVerdict.SweetSpot => $"◆ {_Percent(application)}",
            ApplicationVerdict.Ok => $"● {_Percent(application)}",
            ApplicationVerdict.Adjust => $"▲ {_Percent(application)}",
            ApplicationVerdict.NotMeasurable => "n/a",
            _ => "—"
        };
        return $"{summary} · {main} {label}";
    }

    private static string _BlockSummary(RunHitTallyDto[] tallies, bool withMisses)
    {
        int shots = tallies.Sum(tally => tally.Count);
        int misses = tallies.Where(tally => tally.Quality is HitQuality.Misses).Sum(tally => tally.Count);
        string text = $"{_Number(tallies.Where(tally => tally.Quality is not HitQuality.Misses).Sum(tally => tally.Sum))} hp · {_Number(shots)} shots";
        return withMisses || misses > 0 ? $"{text} · {_Number(misses)} misses" : text;
    }

    private static string _Percent(ApplicationSummary application) =>
        (application.Percent ?? 0).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string _Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
