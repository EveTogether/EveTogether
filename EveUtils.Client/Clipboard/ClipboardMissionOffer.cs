using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using EveUtils.Client.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using EveUtils.Client.Fleet;
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
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Clipboard;

/// <summary>A copied mission Objectives block starts its run outright, the same way one fully-scanned combat site
/// does (ET-158) — no card, and no keyboard taken from EVE. The agent name is the only resolving key (ET-172
/// sub 1/4): it is never parsed from the location line, because the agent name alone is already a sufficient
/// key into the SDE. Starting outright is itself an opt-out (<see cref="AutoStartSettingKey"/>, ET-264): with it
/// off the window still opens prepared — name, rewards, level filled in — and the pilot presses START himself.</summary>
public sealed class ClipboardMissionOffer : ISingletonService, IDisposable
{
    public const string FeatureName = "Mission detection";

    /// <summary>"false" opens the window prepared instead of starting it outright (ET-264). Default on: unset or
    /// "true" keeps today's behaviour.</summary>
    public const string AutoStartSettingKey = "clipboard.mission.auto-start";

    private readonly IToastService _toasts;
    private readonly ISdeAccessor _sde;
    private readonly IDialogService _dialogs;

    // Only here because ActivityWindowViewModel's constructor asks for one; a factory is the upgrade once a second
    // caller needs the same thing.
    private readonly IServiceProvider _services;
    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;
    private readonly TimeProvider _clock;

    private string? _openFingerprint;
    private DateTimeOffset _openFingerprintAtUtc;

    // Same window as ClipboardSignatureOffer, and for the same reason (ET-264): this only has to outlast the two
    // change notifications one real copy can fire, not the minutes a pilot spends on DISCARD's own confirmation
    // dialog. Bounding it in time — instead of the old "until something else is copied" — is what lets copying the
    // same mission text again after a DISCARD start a run again, without this offer having to be told the window
    // closed.
    private static readonly TimeSpan DuplicateNotificationWindow = TimeSpan.FromSeconds(3);

    public ClipboardMissionOffer(ClipboardWatchService clipboardWatch, IToastService toasts, ISdeAccessor sde,
        IDialogService dialogs, IServiceProvider services)
    {
        _toasts = toasts;
        _sde = sde;
        _dialogs = dialogs;
        _services = services;
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _subscription = clipboardWatch.Subscribe(FeatureName, OnCapture);
    }

    public void Dispose() => _subscription.Dispose();

    private void OnCapture(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Mission)
            return;

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Text)));
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            // Same suppress rule as ClipboardSignatureOffer: an identical re-copy within DuplicateNotificationWindow
            // is ignored, so the clipboard watch firing twice for one copy cannot start a second run — but a re-copy
            // later than that (a DISCARD, then copying the same mission again) is a fresh request.
            if (_openFingerprint == fingerprint && now - _openFingerprintAtUtc < DuplicateNotificationWindow)
                return;

            _openFingerprint = fingerprint;
            _openFingerprintAtUtc = now;
        }

        if (ClipboardMissionParser.Parse(capture.Text) is { } mission)
            StartRun(mission, capture.CopiedByCharacter);
    }

    private void StartRun(ClipboardMissionCapture mission, string? copiedByCharacter) =>
        _ = _StartRunAsync(mission, copiedByCharacter);

    /// <summary>ET-264: "Start automatically when copied", per kind, in the existing settings window. Default on
    /// (today's behaviour) — only an explicit "false" prepares the window instead of starting it.</summary>
    private async Task<bool> _StartsAutomaticallyAsync()
    {
        if (_services.GetService<ISettingRepository>() is not { } settings)
            return true;

        foreach (ClientSetting setting in await settings.ListAsync())
            if (setting.Key == AutoStartSettingKey)
                return !string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase);
        return true;
    }

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
            var startsOnArrival = await _StartsAutomaticallyAsync();

            bool answeredAlready = _dialogs.ActivityWindowPilot is not null;
            if (pilot is null && candidates.Count > 1 && !answeredAlready)
            {
                // ET-216: see ClipboardSignatureOffer's own copy of this question for why the copier's own name
                // starts ticked, and why an unmatched one just leaves nothing preselected.
                int? preselectedCharacterId = copiedByCharacter is null
                    ? null
                    : candidates.FirstOrDefault(character =>
                        string.Equals(character.Name, copiedByCharacter, StringComparison.OrdinalIgnoreCase))?.EsiCharacterId;

                // ET-270: see ClipboardSignatureOffer's own copy of this question for why — same rule, same key
                // format (OwnCharacterPickMemory), so a group started off a copied mission and one started off a
                // copied signature restore from the same memory.
                ISettingRepository? settings = _services.GetService<ISettingRepository>();
                IReadOnlyList<FleetParticipant> participation = _services.GetService<IFleetParticipation>()?.Current ?? [];
                long? anchorFleetId = OwnCharacterPickMemory.FleetIdFor(preselectedCharacterId, participation);
                IReadOnlyCollection<int>? fleetCharacterIds =
                    OwnCharacterPickMemory.FleetCharacterIdsFor(preselectedCharacterId, participation);
                IReadOnlyList<int>? preselected = await OwnCharacterPickMemory.ResolvePreselectionAsync(settings,
                    preselectedCharacterId, [.. flying.Select(character => character.EsiCharacterId!.Value)],
                    fleetCharacterIds, anchorFleetId);

                // Multi-select (ET-210): see ClipboardSignatureOffer's own copy of this question for why.
                IReadOnlyList<int>? picked = await _dialogs.PickCharactersAsync("Whose run is this?",
                    [.. candidates.Select(character => new CharacterPickOption(
                        character.EsiCharacterId!.Value, character.Name,
                        flying.Contains(character) ? "EVE client running" : "local character", Enabled: true))],
                    preselected);
                pilot = picked is { Count: > 0 }
                    ? candidates.FirstOrDefault(character => character.EsiCharacterId == picked[0])
                    : null;
                if (picked is { Count: > 1 })
                    additional = [.. picked.Skip(1)
                        .Select(id => candidates.FirstOrDefault(character => character.EsiCharacterId == id))
                        .Where(character => character is not null)
                        .Select(character => character!)];

                if (settings is not null && picked is { Count: > 0 })
                    await OwnCharacterPickMemory.SaveAsync(settings, picked, fleetCharacterIds, anchorFleetId);

                startsOnArrival = startsOnArrival && pilot is not null;
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
                MissionRewardOwnerCharacterId = _ResolveRewardOwner(pilot, additional, copiedByCharacter)?.EsiCharacterId,
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

    /// <summary>Only EVE ever pays a mission's reward to the character who accepted it (ET-260) — the one whose own
    /// clipboard copy started this window. Falls back to the acting pilot when nobody's name matches
    /// <paramref name="copiedByCharacter"/> (an unrecognised or missing capture header) or on a solo mission, where
    /// there is only ever the one candidate anyway.</summary>
    private static Character? _ResolveRewardOwner(Character? pilot, List<Character> additional, string? copiedByCharacter)
    {
        if (copiedByCharacter is null)
            return pilot;

        Character? copier = pilot is not null && string.Equals(pilot.Name, copiedByCharacter, StringComparison.OrdinalIgnoreCase)
            ? pilot
            : additional.FirstOrDefault(character => string.Equals(character.Name, copiedByCharacter, StringComparison.OrdinalIgnoreCase));
        return copier ?? pilot;
    }

    /// <summary>The reward lines a mission's own clipboard capture already carries — never the loot found later, and
    /// never valued by the ISK the clipboard states (ET-172: valuation always runs through ET's own price lookup,
    /// which is why nothing here ever touches a market price).</summary>
    private List<RunParameterInput> _ToParameters(ClipboardMissionCapture mission)
    {
        DateTime now = DateTime.UtcNow;
        var parameters = new List<RunParameterInput>();
        if (mission.IsImportantMission)
            parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.ImportantMission,
                TypedValue = "important mission",
                ObservedAtUtc = now
            });

        if (mission.LocationSystemName is { Length: > 0 } locationSystemName)
            parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.MissionLocation,
                TypedValue = locationSystemName,
                ObservedAtUtc = now
            });

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
