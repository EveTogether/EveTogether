using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// ENEMIES in the run window: one row per enemy type, on its own rather than inside ACTIVITY — a site's worth of rats
/// pushed every other section under the fold (ET-115). The count is typed after the fight, so the rows outlive STOP.
///
/// One collector per character in the group, each fed only by that character's own gamelog (ET-210 review finding,
/// 2026-09-09, round 4: Jithran chose per-character tracking with a group total over one shared tally). Keyed on
/// characterId rather than on which run the window is currently showing, so switching the column never touches a
/// character's own count: nothing here is ever reassigned or cleared for one character because another one was
/// clicked.
/// </summary>
public sealed class EnemiesWindowSectionViewModel : RunWindowSection
{
    private readonly GamelogClientService? _gamelog;
    private readonly Dictionary<int, RunEnemyObservationCollector> _collectors = [];

    public EnemiesWindowSectionViewModel(IRunWindowContext context) : base(context, RunSectionId.Enemies, "ENEMIES")
    {
        _gamelog = context.Services.GetService<GamelogClientService>();
        if (_gamelog is not null)
            _gamelog.CombatObserved += _OnCombatObserved;
    }

    /// <summary>The on-screen character's own sightings — whichever run the column is currently showing. Every other
    /// group member's own collector keeps counting in the background regardless (ET-210 round 4).</summary>
    public IReadOnlyList<RunEnemyObservationViewModel> EnemyObservations =>
        Context.RunCharacterId is { } id && _collectors.TryGetValue(id, out RunEnemyObservationCollector? collector)
            ? collector.Observations
            : [];

    /// <summary>Shut, the section still has to answer both halves of the question it exists for: which kinds were
    /// seen, and how many of them carry a count. A count of zero is not stored (ET-106), so "seen" and "counted"
    /// are different numbers and the header is the only place they are both visible.</summary>
    public override void RefreshSummary()
    {
        int types = EnemyObservations.Count;
        if (types == 0)
        {
            HeaderSummary = Context.RunState == ActivityRunState.NotStarted ? "no run watched yet" : "no enemies seen yet";
            return;
        }

        int counted = EnemyObservations.Count(observation => observation.IsCounted);
        HeaderSummary = $"{types} {(types == 1 ? "type" : "types")} · {(counted == 0 ? "none counted" : $"{counted} counted")}";
    }

    /// <summary>Give the on-screen character its own tally, if it does not have one yet.</summary>
    public override void OnRunStarted()
    {
        if (Context.RunCharacterId is { } id)
            _Ensure(id);

        OnPropertyChanged(nameof(EnemyObservations));
    }

    /// <summary>Called for a sibling the moment its own <c>StartRunCommand</c> is sent, so its gamelog is watched for
    /// enemies from the same instant its bounty and loot start counting (ET-210 review finding, round 4).</summary>
    public override void OnCharacterRunStarted(int characterId) => _Ensure(characterId);

    /// <summary>Let go of every character's list — the whole group's, since STOP, SAVE and DISCARD act on the whole
    /// group (ET-210).</summary>
    public override void OnRunClosed()
    {
        foreach (RunEnemyObservationCollector collector in _collectors.Values)
            collector.Changed -= RefreshSummary;

        _collectors.Clear();
        OnPropertyChanged(nameof(EnemyObservations));
    }

    /// <summary>What SAVE stores for one character — empty when nobody ever typed a count for them, the same "seen,
    /// not counted, never stored" rule <see cref="RunEnemyObservationViewModel.IsCounted"/> already applies live.</summary>
    public override void AddToSave(RunSaveDraft draft)
    {
        if (draft.CharacterId is { } characterId && _collectors.TryGetValue(characterId, out RunEnemyObservationCollector? collector))
            draft.Enemies.AddRange(collector.ToInputs());
    }

    public override void Dispose()
    {
        if (_gamelog is not null)
            _gamelog.CombatObserved -= _OnCombatObserved;
        base.Dispose();
    }

    private void _Ensure(int characterId)
    {
        if (_collectors.ContainsKey(characterId) || Context.Services.GetService<ISdeAccessor>() is not { } sde)
            return;

        var collector = new RunEnemyObservationCollector(characterId,
            name => sde.TryGetTypeId(name, out int typeId) ? typeId : null);
        // Only the summary: re-announcing the list itself while a count is being typed would rebind the editor
        // under the cursor. The rows are an ObservableCollection — the list keeps itself up to date. Wired for
        // every character, not just the one on screen, so a background sibling's count still moves the summary.
        collector.Changed += RefreshSummary;
        _collectors[characterId] = collector;
    }

    // The event fires for damage either way — "250 to Centii Scavenger" and "1 from Centii Servant" alike — and both
    // are the same kind of enemy, so the direction is dropped here rather than carried into the list (ET-115).
    // Routed straight to that character's OWN collector — each already refuses everyone else's id internally, so
    // this only ever widens a row's own observed window, never another character's.
    private void _OnCombatObserved(int characterId, string target, DateTime observedAtUtc, DamageDirection direction)
    {
        if (Context.RunState != ActivityRunState.Running)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            _collectors.GetValueOrDefault(characterId)?.Record(characterId, target, observedAtUtc));
    }
}
