using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// COMBAT on the detail screen (ET-468): what the picked pilot dealt, took and was neuted for over the run, read from
/// the combat SAVE kept (ET-467). A run saved before that says so in one line rather than showing zeros.
/// </summary>
public sealed partial class CombatDetailSectionViewModel : RunDetailSection
{
    private readonly CombatTelemetryChoice _choice;

    public CombatDetailSectionViewModel(RunDetailSectionServices services) : base(RunSectionId.Combat, "COMBAT")
    {
        _choice = services.Combat ?? new CombatTelemetryChoice(services.Dispatcher, services.OwnCharacterIds);
        _choice.PropertyChanged += _OnChoiceChanged;
        HeaderSummary = "no combat recorded";
    }

    public ObservableCollection<CombatPilotChipViewModel> Pilots => _choice.Pilots;

    public ObservableCollection<CombatTileViewModel> Tiles { get; } = [];

    [ObservableProperty] private string? _combatEmptyText;

    public override bool HasContent => _choice.HasAny;

    public override void Apply(RunDetailSectionInput input)
    {
    }

    public override Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken) =>
        _choice.LoadAsync(input, cancellationToken);

    private void _OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CombatTelemetryChoice.Shown))
        {
            return;
        }

        Tiles.Clear();
        if (_choice.Shown is not { } timeline)
        {
            HeaderSummary = "no combat recorded";
            CombatEmptyText = NotRecordedText;
            return;
        }

        CombatEmptyText = null;
        foreach (CombatTileViewModel tile in TilesOf(timeline))
        {
            Tiles.Add(tile);
        }
        HeaderSummary = $"{_Number(_Total(timeline, CombatSeriesKind.DmgOut))} hp dealt · "
            + $"{_Number(_Total(timeline, CombatSeriesKind.DmgIn))} taken · {_choice.ShownName}";
    }

    internal const string NotRecordedText =
        "No combat was recorded for this run: it was saved before EVE Together kept the game log's combat with the run.";

    private static IEnumerable<CombatTileViewModel> TilesOf(RunCombatTimelineDto timeline)
    {
        int seconds = Math.Max(1, timeline.Seconds - 1);
        long dealt = _Total(timeline, CombatSeriesKind.DmgIn);
        long taken = _Total(timeline, CombatSeriesKind.DmgIn);
        yield return new("DAMAGE DEALT", $"{_Number(dealt)} hp",
            $"avg {_Number(dealt / seconds)} dps over {TimeSpan.FromSeconds(seconds):mm\\:ss}");
        yield return new("DAMAGE TAKEN", $"{_Number(taken)} hp",
            $"avg {_Number(taken / seconds)} dps · {_Number(timeline.HitsIn)} hits on you");
        yield return new("MAX HIT DEALT", $"{_Number(timeline.MaxHitOut)} hp", _MaxHitOutNote(timeline));
        yield return new("MAX HIT TAKEN", $"{_Number(timeline.MaxHitIn)} hp", _MaxHitInNote(timeline));
        yield return new("NEUTED (ON YOU)", $"{_Number(_Total(timeline, CombatSeriesKind.NeutIn))} GJ",
            timeline.Series.ContainsKey(CombatSeriesKind.NeutIn) ? "energy neutralized on you" : "no neut line in this run");
        yield return new("REPS", $"{_Number(_Total(timeline, CombatSeriesKind.RepIn) + _Total(timeline, CombatSeriesKind.RepOut))} hp",
            timeline.Series.ContainsKey(CombatSeriesKind.RepIn) || timeline.Series.ContainsKey(CombatSeriesKind.RepOut)
                ? $"in {_Number(_Total(timeline, CombatSeriesKind.RepIn))} · out {_Number(_Total(timeline, CombatSeriesKind.RepOut))}"
                : "no rep line in this run");
        yield return new("CAP / NEUT OUT", $"{_Number(_Total(timeline, CombatSeriesKind.CapOut) + _Total(timeline, CombatSeriesKind.NeutOut))} GJ",
            timeline.Series.ContainsKey(CombatSeriesKind.CapOut) || timeline.Series.ContainsKey(CombatSeriesKind.NeutOut)
                ? $"cap {_Number(_Total(timeline, CombatSeriesKind.CapOut))} · neut {_Number(_Total(timeline, CombatSeriesKind.NeutOut))}"
                : "no such line in this run");
        int shots = timeline.HitsIn + timeline.MissesIn;
        yield return new("MISSES ON YOU", _Number(timeline.MissesIn),
            shots == 0 ? "no enemy shot in this run" : $"of {_Number(shots)} enemy shots · {100 * timeline.MissesIn / shots} %");
    }

    // The weapon is not kept with the biggest hit; the tally that holds it says which one it was.
    private static string _MaxHitOutNote(RunCombatTimelineDto timeline) =>
        timeline.MaxHitOutTarget is not { } target
            ? "no hit dealt"
            : timeline.HitTallies.FirstOrDefault(tally => tally.Direction is DamageDirection.Outgoing
                && tally.Counterparty == target && tally.Max == timeline.MaxHitOut)?.Weapon is { } weapon
                ? $"{weapon} on {target}"
                : $"on {target}";

    private static string _MaxHitInNote(RunCombatTimelineDto timeline) =>
        timeline.MaxHitInSource is not { } source
            ? "no hit taken"
            : timeline.HitTallies.FirstOrDefault(tally => tally.Direction is DamageDirection.Incoming
                && tally.Counterparty == source && tally.Max == timeline.MaxHitIn) is { } tally
                ? $"{source} · {_Word(tally.Quality)}"
                : source;

    private static string _Word(HitQuality quality) => quality is HitQuality.Glances ? "Glances Off" : quality.ToString();

    private static long _Total(RunCombatTimelineDto timeline, CombatSeriesKind kind) =>
        timeline.Series.TryGetValue(kind, out int[]? perSecond) ? perSecond.Sum(value => (long)value) : 0;

    private static string _Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
