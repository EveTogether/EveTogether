using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using EveUtils.Client.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.Clipboard;

/// <summary>A copied mission Objectives block starts its run outright, the same way one fully-scanned combat site
/// does (ET-158) — no card, and no keyboard taken from EVE. The agent name is the only resolving key (ET-172
/// sub 1/4): it is never parsed from the location line, because the agent name alone is already a sufficient
/// key into the SDE.</summary>
public sealed class ClipboardMissionOffer : ISingletonService, IDisposable
{
    public const string FeatureName = "Mission detection";

    private readonly IToastService _toasts;
    private readonly ISdeAccessor _sde;
    private readonly IDialogService _dialogs;

    // Only here because ActivityWindowViewModel's constructor asks for one; a factory is the upgrade once a second
    // caller needs the same thing.
    private readonly IServiceProvider _services;
    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;

    private string? _openFingerprint;

    public ClipboardMissionOffer(ClipboardWatchService clipboardWatch, IToastService toasts, ISdeAccessor sde,
        IDialogService dialogs, IServiceProvider services)
    {
        _toasts = toasts;
        _sde = sde;
        _dialogs = dialogs;
        _services = services;
        _subscription = clipboardWatch.Subscribe(FeatureName, OnCapture);
    }

    public void Dispose() => _subscription.Dispose();

    private void OnCapture(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Mission)
            return;

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Text)));
        lock (_gate)
        {
            // Same suppress rule as ClipboardSignatureOffer: an identical re-copy is ignored until something else is
            // copied, so the clipboard watch firing twice for one copy cannot start a second run.
            if (_openFingerprint == fingerprint)
                return;

            _openFingerprint = fingerprint;
        }

        if (ClipboardMissionParser.Parse(capture.Text) is { } mission)
            StartRun(mission, capture.CopiedByCharacter);
    }

    private void StartRun(ClipboardMissionCapture mission, string? copiedByCharacter) =>
        _ = _StartRunAsync(mission, copiedByCharacter);

    private async Task _StartRunAsync(ClipboardMissionCapture mission, string? copiedByCharacter)
    {
        try
        {
            var registry = _services.GetService<ICharacterRegistry>();
            List<Character> known = registry is null
                ? []
                : (await registry.GetAllAsync()).Where(character => character.EsiCharacterId is not null).ToList();
            List<Character> flying = InGameCharacters.Among(known, _services.GetService<ILocalCharacterPresence>());
            List<Character> candidates = flying.Count == 0 ? known : flying;

            Character? pilot = candidates is [{ } only] ? only : null;
            List<Character> additional = [];
            var startsOnArrival = true;

            bool answeredAlready = _dialogs.ActivityWindowPilot is not null;
            if (pilot is null && candidates.Count > 1 && !answeredAlready)
            {
                // ET-216: see ClipboardSignatureOffer's own copy of this question for why the copier's own name
                // starts ticked, and why an unmatched one just leaves nothing preselected.
                int? preselectedCharacterId = copiedByCharacter is null
                    ? null
                    : candidates.FirstOrDefault(character =>
                        string.Equals(character.Name, copiedByCharacter, StringComparison.OrdinalIgnoreCase))?.EsiCharacterId;

                // Multi-select (ET-210): see ClipboardSignatureOffer's own copy of this question for why.
                IReadOnlyList<int>? picked = await _dialogs.PickCharactersAsync("Whose run is this?",
                    [.. candidates.Select(character => new CharacterPickOption(
                        character.EsiCharacterId!.Value, character.Name,
                        flying.Contains(character) ? "EVE client running" : "local character", Enabled: true))],
                    preselectedCharacterId is { } preselectedId ? [preselectedId] : null);
                pilot = picked is { Count: > 0 }
                    ? candidates.FirstOrDefault(character => character.EsiCharacterId == picked[0])
                    : null;
                if (picked is { Count: > 1 })
                    additional = [.. picked.Skip(1)
                        .Select(id => candidates.FirstOrDefault(character => character.EsiCharacterId == id))
                        .Where(character => character is not null)
                        .Select(character => character!)];
                startsOnArrival = pilot is not null;
            }

            // The agent name is the sole resolving key (ET-172 sub 1) — no letter of the location line is ever
            // parsed. An agent the SDE import does not know does not block the run; it just starts without a level.
            SdeAgent? agent = mission.AgentName is null ? null : _sde.FindAgentByName(mission.AgentName);
            if (mission.AgentName is not null && agent is null)
                _toasts.Show("Agent not recognised",
                    $"{mission.AgentName} is not in the SDE import, so this run started without a level.", ToastKind.Information);

            var window = new ActivityWindowViewModel(ActivityKind.Mission, _services)
            {
                // Mission captures lack a site name. Prefer the reported agent, then the Objectives header.
                SignatureName = mission.AgentName ?? mission.ObjectivesHeaderName,
                MissionAgentId = agent?.AgentId,
                MissionLevel = agent?.Level,
                MissionSolarSystemId = agent?.SolarSystemId,
                SolarSystem = agent?.SolarSystemName,
                PendingParameters = _ToParameters(mission),
                StartsOnArrival = startsOnArrival
            };
            if (pilot is { EsiCharacterId: { } characterId })
                window.UseCharacter(characterId, pilot.Name);
            if (additional.Count > 0)
                window.UseAdditionalCharacters(
                    [.. additional.Select(character => (character.EsiCharacterId!.Value, character.Name))]);

            _dialogs.ShowActivityWindow(window, RunWindowOpenTrigger.CopiedFromClipboard);
        }
        catch (Exception ex)
        {
            _toasts.Show("Run not started", $"Could not open the run on {mission.AgentName}: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>The reward lines a mission's own clipboard capture already carries — never the loot found later, and
    /// never valued by the ISK the clipboard states (ET-172: valuation always runs through ET's own price lookup,
    /// which is why nothing here ever touches a market price).</summary>
    private List<RunParameterInput> _ToParameters(ClipboardMissionCapture mission)
    {
        DateTime now = DateTime.UtcNow;
        var parameters = new List<RunParameterInput>();
        foreach (ClipboardMissionReward reward in mission.Rewards)
        {
            RunParameterKey key = reward.ParameterKey ?? RunParameterKey.Unknown;
            bool isItem = reward.ItemName is not null;
            string unit = key == RunParameterKey.LoyaltyPoints ? "LP" : "ISK";
            parameters.Add(new RunParameterInput
            {
                ParameterKey = key,
                // Invariant: this is stored data read back later, not UI text — the machine's own culture must not
                // decide whether the decimal separator is a dot or a comma.
                TypedValue = reward.ParameterKey is null
                    ? reward.RawLine
                    : isItem
                        ? FormattableString.Invariant($"{reward.ItemQuantity} x {reward.ItemName}")
                        : FormattableString.Invariant($"{reward.Amount} {unit}"),
                Amount = isItem ? (decimal?)reward.ItemQuantity : reward.Amount,
                ItemTypeId = reward.ItemName is { } name && _sde.TryGetTypeId(name, out var typeId) ? typeId : null,
                BonusWindowSeconds = key == RunParameterKey.BonusIsk ? mission.BonusWindowSeconds : null,
                ObservedAtUtc = now
            });
        }
        return parameters;
    }
}
