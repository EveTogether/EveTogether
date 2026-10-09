using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Activity;

public sealed partial class ActivityWindowViewModel
{
    private sealed record NextRun(int CharacterId, string CharacterName, IReadOnlyList<(int Id, string Name)> Additional);

    /// <summary>
    /// "Run another?" after an abyssal run is saved (ET-493). Arming is not a mechanism of its own: an armed run is a
    /// fresh window in <see cref="ActivityRunState.NotStarted"/>, the very thing the manual start hands over, so the
    /// answer only names the same pilot(s) again. Tier and weather come back through the remembered settings, and a
    /// commander's window offers the fleet the way every armed window does (<see cref="_OfferPreparedRunToFleet"/>);
    /// members follow that offer, which is why they are never asked.
    /// </summary>
    private async Task<NextRun?> _AskForNextRunAsync(CqrsDispatcher dispatcher)
    {
        if (Kind != ActivityKind.Abyssal || _pendingCopy is not null || FleetId is not null && !Authority.IsFleetCommander
            || _runCharacterId is not { } characterId || _runCharacterName is not { } characterName
            || _services.GetService<IDialogService>() is not { } dialogs)
            return null;

        List<(int Id, string Name)> additional = [.. Participants
            .Where(participant => participant.CharacterId != characterId)
            .Select(participant => (participant.CharacterId, participant.CharacterName))];

        var pilots = additional.Select(extra => (long)extra.Id).Append(characterId).ToHashSet();
        var running = await Task.Run(() => dispatcher.Query(new GetRunningRunsQuery()));
        if (running.Value?.Any(run => pilots.Contains(run.CharacterId)) == true)
            return null;

        bool? another = await dialogs.ChooseAsync("Run another?",
            "Arm the next abyssal run with the same pilot and filament settings. It starts when you jump in.",
            "Arm next run", "Done", this);
        return another == true ? new NextRun(characterId, characterName, additional) : null;
    }

    private void _ArmNextRun(NextRun next, IDialogService dialogs)
    {
        var window = new ActivityWindowViewModel(ActivityKind.Abyssal, _services);
        window.UseCharacter(next.CharacterId, next.CharacterName);
        if (next.Additional.Count > 0)
            window.UseAdditionalCharacters(next.Additional);
        dialogs.ShowActivityWindow(window);
    }
}
