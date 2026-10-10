using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Platform;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Grouping;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// Another own character in the pocket while this window's run is going, without a leg in it (ET-500): logged in
/// mid-run and put in the fleet, or picked too late. Adding one used to be a click on the header chip and a picker,
/// and a toon already inside when it was picked never started at all — its leg waited for a crossing this client
/// had already missed. Offered on sight instead, one click for all of them.
/// </summary>
public sealed partial class ActivityWindowViewModel
{
    // Started on a click and not yet back in Participants, so the next tick does not offer them a second time.
    private readonly HashSet<int> _lateJoining = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLateJoinShown), nameof(LateJoinText))]
    private IReadOnlyList<(int Id, string Name)> _lateJoiners = [];

    public bool IsLateJoinShown => LateJoiners.Count > 0;

    public string? LateJoinText => LateJoiners.Count == 0
        ? null
        : $"{string.Join(", ", LateJoiners.Select(character => character.Name))} "
          + $"{(LateJoiners.Count == 1 ? "is" : "are")} in the pocket but not in this run.";

    partial void OnLateJoinersChanged(IReadOnlyList<(int Id, string Name)> value) => _RefreshCompactNoticeFlags();

    private void _RefreshLateJoiners()
    {
        _lateJoining.RemoveWhere(id => Participants.Any(participant => participant.CharacterId == id));
        int? acting = _ActingCharacterId();
        List<(int Id, string Name)> inside = !RunType.ClockPerPilot || RunState is not ActivityRunState.Running
                                             || RunId is null
            ? []
            : [.. RunCharacters
                .Where(row => row.IsEsiLinked && row.CharacterId != acting
                              && !_lateJoining.Contains(row.CharacterId)
                              && Participants.All(participant => participant.CharacterId != row.CharacterId)
                              && _IsInsideUnannounced(row.CharacterId, row.Name))
                .Select(row => (row.CharacterId, row.Name))];
        if (!inside.SequenceEqual(LateJoiners))
            LateJoiners = inside;
    }

    // A toon still waiting for its own crossing (_ownLegsPending) is left to it while that crossing can still be seen;
    // only one that came up already inside, with no anchor to cross on, is offered.
    private bool _IsInsideUnannounced(int characterId, string name)
    {
        if (_gamelog is null
            || _services.GetService<ILocalCharacterPresence>()?.IsInGame(characterId, name) is false)
            return false;

        CharacterMetricsSnapshot snapshot = _gamelog.Snapshot(name);
        bool isPending = _ownLegsPending.Any(pending => pending.Id == characterId);
        return snapshot.AbyssalAnchor is not null ? !isPending : snapshot.IsSeenInsideAbyssal;
    }

    /// <summary>Each offered toon gets its own leg under this run's group code, from its own way in when this client
    /// saw it, otherwise from now — the join is the earliest moment this client can vouch for.</summary>
    [RelayCommand]
    private async Task JoinRunningRunAsync()
    {
        if (LateJoiners.Count == 0 || RunId is not { } runId || _gamelog is null
            || _services.GetService<CqrsDispatcher>() is null)
            return;

        // Held until each one shows up in Participants (_RefreshLateJoiners), which is read after its row exists — a
        // tick in between would otherwise offer them again. One that fails to start says so in its own toast, and the
        // header chip still adds it by hand.
        List<(int Id, string Name)> joining = [.. LateJoiners];
        _lateJoining.UnionWith(joining.Select(character => character.Id));
        LateJoiners = [];

        if (GroupCode is null)
        {
            using IServiceScope scope = _services.CreateScope();
            GroupCode = RunGroupCode.Create();
            await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Send(new LinkRunToGroupCodeCommand(runId, GroupCode, FleetId));
        }

        DateTime nowUtc = DateTime.UtcNow;
        foreach ((int Id, string Name) character in joining)
        {
            _ownLegsPending.RemoveAll(pending => pending.Id == character.Id);
            _ownLegWasInside[character.Id] = true;
            await _StartOwnPilotLegAsync(character.Id, character.Name,
                _gamelog.Snapshot(character.Name).AbyssalAnchor ?? nowUtc);
        }

        Refresh(DateTime.UtcNow);
    }
}
