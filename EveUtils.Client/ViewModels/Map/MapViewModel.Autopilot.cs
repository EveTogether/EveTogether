using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Map;
using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Client.ViewModels.Map;

/// <summary>
/// Setting the in-game autopilot from the map (ET-525): SET DESTINATION on the selected system, ADD WAYPOINT behind
/// Shift+click and the right-click menu. Who gets it is decided by <see cref="MapAutopilotTarget"/>; the followed
/// character is the target without any extra click.
/// </summary>
public sealed partial class MapViewModel
{
    private WaypointMode? _pendingMode;

    public ObservableCollection<MapAutopilotCandidate> AutopilotChoices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetDestinationCommand), nameof(AddWaypointCommand))]
    private bool _isAutopilotBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutopilotMessage))]
    private string _autopilotMessage = string.Empty;

    [ObservableProperty] private bool _isAutopilotChoosing;

    public bool HasAutopilotMessage => AutopilotMessage.Length > 0;

    public bool CanSetAutopilot => _autopilot is not null;

    public string AutopilotButtonText => _AutopilotTarget().Plan is MapAutopilotPlan.Allow or MapAutopilotPlan.ChooseToAllow
        ? "ALLOW SET DESTINATION…"
        : "SET DESTINATION";

    public string AutopilotChoiceTitle => _pendingMode == WaypointMode.AddWaypoint ? "Add the waypoint for:" : "Set the destination for:";

    public string AutopilotTip
    {
        get
        {
            MapAutopilotTarget target = _AutopilotTarget();
            const string GameRoute = "The game plans the route with its own autopilot settings, not this map's route preference.";
            return target.Plan switch
            {
                MapAutopilotPlan.Send =>
                    $"Set autopilot destination for {target.Candidates[0].Name}.\nShift+click or right-click adds it as a waypoint instead.\n{GameRoute}",
                MapAutopilotPlan.Choose =>
                    $"Set autopilot destination: you pick which character.\nShift+click or right-click adds it as a waypoint instead.\n{GameRoute}",
                MapAutopilotPlan.Allow =>
                    $"{target.Candidates[0].Name} has not allowed setting the destination ({AutopilotScopeCatalog.WriteWaypoint}).\nALLOW… opens the scope picker with it ticked, then EVE's sign-in.",
                MapAutopilotPlan.ChooseToAllow =>
                    $"No character has allowed setting the destination ({AutopilotScopeCatalog.WriteWaypoint}).\nALLOW… lets you pick one, then opens the scope picker with it ticked.",
                _ => "Sign a character in to ESI to set the autopilot destination."
            };
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunAutopilot))]
    private Task SetDestinationAsync() => _StartAutopilotAsync(WaypointMode.SetDestination);

    [RelayCommand(CanExecute = nameof(CanRunAutopilot))]
    private Task AddWaypointAsync() => _StartAutopilotAsync(WaypointMode.AddWaypoint);

    private bool CanRunAutopilot() => !IsAutopilotBusy;

    [RelayCommand]
    private Task ChooseAutopilotCharacterAsync(MapAutopilotCandidate candidate)
    {
        WaypointMode mode = _pendingMode ?? WaypointMode.SetDestination;
        _CloseAutopilotChoice();
        return _RunAutopilotAsync(candidate, mode);
    }

    [RelayCommand]
    private void CancelAutopilotChoice() => _CloseAutopilotChoice();

    partial void OnFollowedCharacterChanged(MapCharacterRowViewModel? value) => _RaiseAutopilotTargetChanged();

    partial void OnSelectedIndexChanged(int value)
    {
        _CloseAutopilotChoice();
        AutopilotMessage = string.Empty;
    }

    private void _RaiseAutopilotTargetChanged()
    {
        OnPropertyChanged(nameof(AutopilotButtonText));
        OnPropertyChanged(nameof(AutopilotTip));
    }

    private MapAutopilotTarget _AutopilotTarget()
    {
        IReadOnlyList<MapAutopilotCandidate> own = [.. Characters.Select(_CandidateOf)];
        MapAutopilotCandidate? followed = IsFollowingCharacter && FollowedCharacter is { } row ? _CandidateOf(row) : null;
        return MapAutopilotTarget.Resolve(followed, own);
    }

    private MapAutopilotCandidate _CandidateOf(MapCharacterRowViewModel row) =>
        new(row.CharacterId, row.Name,
            _characters.Any(character => character.EsiCharacterId == row.CharacterId && character.HasScope(AutopilotScopeCatalog.WriteWaypoint)),
            _positions.IsOffline(row.CharacterId));

    private Task _StartAutopilotAsync(WaypointMode mode)
    {
        if (_autopilot is null || SelectedSystem is null || IsAutopilotBusy)
            return Task.CompletedTask;

        AutopilotMessage = string.Empty;
        _CloseAutopilotChoice();
        MapAutopilotTarget target = _AutopilotTarget();
        switch (target.Plan)
        {
            case MapAutopilotPlan.Choose or MapAutopilotPlan.ChooseToAllow:
                _pendingMode = mode;
                foreach (MapAutopilotCandidate candidate in target.Candidates)
                    AutopilotChoices.Add(candidate);
                IsAutopilotChoosing = true;
                OnPropertyChanged(nameof(AutopilotChoiceTitle));
                return Task.CompletedTask;
            case MapAutopilotPlan.NoCharacter:
                AutopilotMessage = "No character of yours is signed in to ESI.";
                return Task.CompletedTask;
            default:
                return _RunAutopilotAsync(target.Candidates[0], mode);
        }
    }

    private async Task _RunAutopilotAsync(MapAutopilotCandidate candidate, WaypointMode mode)
    {
        if (_autopilot is null || SelectedSystem is not { } system || IsAutopilotBusy)
            return;

        IsAutopilotBusy = true;
        try
        {
            if (!candidate.HasScope)
            {
                await _AllowAutopilotAsync(candidate);
                return;
            }

            EsiResult result = await _autopilot.SetWaypointAsync(candidate.CharacterId, system.SolarSystemId, mode);
            if (!result.IsSuccess)
            {
                AutopilotMessage = MapAutopilotMessages.Failure(result.Error, candidate.Name);
                if (MapAutopilotMessages.NeedsAllow(result.Error))
                    await _AllowAutopilotAsync(candidate);
                return;
            }

            string done = mode == WaypointMode.AddWaypoint
                ? $"Waypoint added for {candidate.Name}: {system.Name}"
                : $"Destination set for {candidate.Name}: {system.Name}";
            AutopilotMessage = candidate.IsOffline
                ? $"{done}. {candidate.Name} looks offline: the EVE client has to be running with that character."
                : done;
        }
        finally
        {
            IsAutopilotBusy = false;
        }
    }

    private Task _AllowAutopilotAsync(MapAutopilotCandidate candidate) =>
        _allowScope?.Invoke(candidate.CharacterId, AutopilotScopeCatalog.WriteWaypoint) ?? Task.CompletedTask;

    private void _CloseAutopilotChoice()
    {
        AutopilotChoices.Clear();
        IsAutopilotChoosing = false;
        _pendingMode = null;
    }
}
