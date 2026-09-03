using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;

namespace EveUtils.Client.Runs;

/// <summary>
/// The fleet commander starts the run and every member is OFFERED it (ET-105). Opening the window is the whole of
/// joining — it carries the commander's group code, so the member's run is filed under the same group — which means
/// declining has to leave nothing at all behind, and it does: no window is built and no run row is created.
///
/// Two shapes, the member's choice (<see cref="AutoOpenSettingKey"/>), differing only in WHEN the window opens:
/// an offer they accept (default), or the window straight away as it behaved before. Either way this is the only
/// caller that passes <see cref="RunWindowOpenTrigger.RemoteFleetCommander"/>: the one path where a window appears
/// because somebody else acted, and it must not take the keyboard from a pilot who is mid-fight in EVE.
///
/// The offer stays up until the pilot answers it — a toast carrying buttons never auto-expires — and nothing else
/// takes it away, not even the commander ending the run. A card that removes itself is a card the pilot can miss,
/// and missing it is missing the group, which is the whole point of the feature. Accepting an offer whose run has
/// since ended is refused with a toast that says so, rather than opening a window onto a dead group code.
/// </summary>
public sealed class FleetRunWindowPresenter : ISingletonService, IDisposable
{
    /// <summary>"true" opens the window the moment the commander starts, as it did before the offer. Default off.</summary>
    public const string AutoOpenSettingKey = "fleet.run-window.auto-open";

    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly IDisposable _subscription;
    private readonly IDisposable _discardSubscription;
    private readonly HashSet<string> _endedGroupCodes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public FleetRunWindowPresenter(IEventBus eventBus, IDialogService dialogs, IServiceProvider services)
    {
        _dialogs = dialogs;
        _services = services;
        _subscription = eventBus.Subscribe<FleetRunGroupCodeEvent>(_OnFleetRunStartedAsync);
        _discardSubscription = eventBus.Subscribe<FleetRunDiscardedEvent>(_OnFleetRunEndedAsync);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _discardSubscription.Dispose();
    }

    private async Task _OnFleetRunStartedAsync(FleetRunGroupCodeEvent integrationEvent, CancellationToken cancellationToken)
    {
        // Only the commander's start reaches everybody. A member's own start is their own business.
        if (!integrationEvent.Data.IsFleetCommander)
            return;

        if (await _AutoOpensAsync(cancellationToken))
        {
            _Open(integrationEvent.Data);
            return;
        }

        // A window already up is the commander's own client, or a member already in a run: there is nothing to
        // offer, which is the same answer RunWindowPresentation gives that case.
        if (!_dialogs.IsActivityWindowOpen)
            _Offer(integrationEvent.Data);
    }

    private Task _OnFleetRunEndedAsync(FleetRunDiscardedEvent integrationEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
            _endedGroupCodes.Add(integrationEvent.Data.GroupCode);
        return Task.CompletedTask;
    }

    private async Task<bool> _AutoOpensAsync(CancellationToken cancellationToken)
    {
        if (_services.GetService<ISettingRepository>() is not { } settings)
            return false;

        foreach (ClientSetting setting in await settings.ListAsync(cancellationToken))
            if (setting.Key == AutoOpenSettingKey)
                return string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private void _Offer(RunGroupCodeStart start) =>
        Dispatcher.UIThread.Post(() => _services.GetService<IToastService>()?.Show(
            "Fleet run started",
            _Where(start),
            ToastKind.Information,
            [new ToastAction("Join run", () => _Accept(start), ToastActionStyle.Affirmative)]));

    private void _Accept(RunGroupCodeStart start)
    {
        bool ended;
        lock (_gate)
            ended = _endedGroupCodes.Contains(start.GroupCode);

        if (ended)
        {
            _services.GetService<IToastService>()?.Show("Fleet run already ended",
                $"{_Where(start)} — the commander ended it before you joined.", ToastKind.Information);
            return;
        }

        _ = _AcceptAsync(start);
    }

    /// <summary>
    /// Which pilot registers this run, asked only where the question is real: two or more of this client's
    /// characters with an EVE client actually up. That single count carries both halves of "more than one client
    /// AND more than one character" — one client shows one character, so two characters in game are two clients —
    /// and it can never put up a dialog that answers itself.
    ///
    /// Fewer than two goes straight through, which is also what a probe that can see less than it should
    /// (Wayland, an unsupported platform) degrades to: the window opens as before and the pilot is asked at START
    /// by <c>_ResolveCharacterAsync</c>, over every character rather than only the flying ones.
    /// </summary>
    private async Task _AcceptAsync(RunGroupCodeStart start)
    {
        IReadOnlyList<Character> flying = await _FlyingCharactersAsync();
        if (flying.Count < 2)
        {
            _Open(start);
            return;
        }

        int? picked = await _dialogs.PickCharacterAsync("Who is registering this run?",
            [.. flying.Select(character => new CharacterPickOption(
                character.EsiCharacterId!.Value, character.Name, "EVE client running", Enabled: true))]);

        // Dismissed is declined, exactly like dismissing the offer itself: nothing opens and nothing is created.
        if (picked is { } characterId)
            _Open(start, flying.First(character => character.EsiCharacterId == characterId));
    }

    /// <summary>
    /// The pilots this offer may name, by the shared <see cref="InGameCharacters"/> rule the run window's own
    /// START question uses. The one thing decided differently here: START falls back to every character when it
    /// detects nobody, because it cannot record a run without one — this can, so an empty answer means "do not
    /// ask", not "ask about everybody".
    /// </summary>
    private async Task<IReadOnlyList<Character>> _FlyingCharactersAsync() =>
        _services.GetService<ICharacterRegistry>() is not { } registry
            ? []
            : InGameCharacters.Among(await registry.GetAllAsync(), _services.GetService<ILocalCharacterPresence>());

    private void _Open(RunGroupCodeStart start, Character? pilot = null)
    {
        ActivityKind kind = start.ActivityKind == StoredActivityKind.Abyssal ? ActivityKind.Abyssal : ActivityKind.Site;
        Dispatcher.UIThread.Post(() =>
        {
            ActivityWindowViewModel window = new(kind, _services);
            // The pilot first: joining creates this member's own run row, and that row is filed under whoever this
            // window is for.
            if (pilot is { EsiCharacterId: { } characterId })
                window.UseCharacter(characterId, pilot.Name);
            window.JoinFleetRun(start);
            _dialogs.ShowActivityWindow(window, RunWindowOpenTrigger.RemoteFleetCommander);
        });
    }

    private static string _Where(RunGroupCodeStart start) =>
        string.Join(" · ", new[] { start.SiteName, start.SolarSystemName }
            .Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } named
            ? named
            : "Site and system not known";
}
