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
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;

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
///
/// The one exception is an offer made before anybody went in (ET-246, <see cref="FleetRunGroupPreparedEvent"/>):
/// there is no run behind it yet to miss, so the commander calling it off takes the card down. The real start, when
/// it comes, replaces that card rather than standing beside it.
/// </summary>
public sealed class FleetRunWindowPresenter : ISingletonService, IDisposable
{
    /// <summary>"true" opens the window the moment the commander starts, as it did before the offer. Default off.</summary>
    public const string AutoOpenSettingKey = "fleet.run-window.auto-open";

    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly IDisposable _subscription;
    private readonly IDisposable _preparedSubscription;
    private readonly IDisposable _discardSubscription;
    private readonly HashSet<string> _endedGroupCodes = new(StringComparer.Ordinal);
    // The group codes whose card on screen is still the prepared one — the only cards a call-off takes down.
    private readonly HashSet<string> _preparedOffers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public FleetRunWindowPresenter(IEventBus eventBus, IDialogService dialogs, IServiceProvider services)
    {
        _dialogs = dialogs;
        _services = services;
        _subscription = eventBus.Subscribe<FleetRunGroupCodeEvent>((integrationEvent, cancellationToken) =>
            _OnCommanderOfferAsync(new Offer(integrationEvent.Data, IsPrepared: false), cancellationToken));
        _preparedSubscription = eventBus.Subscribe<FleetRunGroupPreparedEvent>((integrationEvent, cancellationToken) =>
            _OnCommanderOfferAsync(new Offer(integrationEvent.Data, IsPrepared: true), cancellationToken));
        _discardSubscription = eventBus.Subscribe<FleetRunDiscardedEvent>(_OnFleetRunEndedAsync);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _preparedSubscription.Dispose();
        _discardSubscription.Dispose();
    }

    private async Task _OnCommanderOfferAsync(Offer offer, CancellationToken cancellationToken)
    {
        // Only the commander's start reaches everybody. A member's own start is their own business.
        if (!offer.Start.IsFleetCommander)
            return;

        if (await _AutoOpensAsync(cancellationToken))
        {
            _Open(offer);
            return;
        }

        // A window already up is the commander's own client, or a member already in a run: there is nothing to
        // offer, which is the same answer RunWindowPresentation gives that case.
        if (!_dialogs.IsActivityWindowOpen)
            _Offer(offer);
    }

    private Task _OnFleetRunEndedAsync(FleetRunDiscardedEvent integrationEvent, CancellationToken cancellationToken)
    {
        string groupCode = integrationEvent.Data.GroupCode;
        bool withdrawn;
        lock (_gate)
        {
            _endedGroupCodes.Add(groupCode);
            withdrawn = _preparedOffers.Remove(groupCode);
        }

        if (withdrawn)
            _services.GetService<IToastService>()?.Dismiss(_OfferKey(groupCode));
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

    private void _Offer(Offer offer)
    {
        lock (_gate)
            if (offer.IsPrepared)
                _preparedOffers.Add(offer.Start.GroupCode);
            else
                _preparedOffers.Remove(offer.Start.GroupCode);

        Dispatcher.UIThread.Post(() => _services.GetService<IToastService>()?.Show(
            offer.IsPrepared ? "Fleet run prepared" : "Fleet run started",
            offer.IsPrepared ? $"{_Where(offer.Start)} — your run starts when you jump in" : _Where(offer.Start),
            ToastKind.Information,
            [new ToastAction("Join run", () => _Accept(offer), ToastActionStyle.Affirmative)],
            onClosed: null, replacementKey: _OfferKey(offer.Start.GroupCode)));
    }

    private static string _OfferKey(string groupCode) => $"fleet-run-offer:{groupCode}";

    private void _Accept(Offer offer)
    {
        bool ended;
        lock (_gate)
            ended = _endedGroupCodes.Contains(offer.Start.GroupCode);

        if (ended)
        {
            _services.GetService<IToastService>()?.Show("Fleet run already ended",
                $"{_Where(offer.Start)} — the commander ended it before you joined.", ToastKind.Information);
            return;
        }

        _ = _AcceptAsync(offer);
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
    private async Task _AcceptAsync(Offer offer)
    {
        IReadOnlyList<Character> flying = await _FlyingCharactersAsync();
        if (flying.Count < 2)
        {
            _Open(offer);
            return;
        }

        // Multi-select (ET-210): multiboxing several of these toons onto the commander's own site is exactly as
        // real as one, and the window they all land on files each answer under its own run, sharing the commander's
        // group code — FleetRunGroupCodeCoordinator already treats N members starting on one code as ordinary.
        IReadOnlyList<int>? picked = await _dialogs.PickCharactersAsync("Who is registering this run?",
            [.. flying.Select(character => new CharacterPickOption(
                character.EsiCharacterId!.Value, character.Name, "EVE client running", Enabled: true))]);

        // Dismissed is declined, exactly like dismissing the offer itself: nothing opens and nothing is created.
        if (picked is not { Count: > 0 })
            return;

        Character pilot = flying.First(character => character.EsiCharacterId == picked[0]);
        IReadOnlyList<Character> additional =
            [.. flying.Where(character => character.EsiCharacterId is { } id && picked.Skip(1).Contains(id))];
        _Open(offer, pilot, additional);
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

    private void _Open(Offer offer, Character? pilot = null, IReadOnlyList<Character>? additional = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // The commander's kind, passed on as it came. It used to be squeezed through "abyssal or else a site"
            // here, which is how a remote start of any other kind arrived as a site (ET-174 AC-3).
            ActivityWindowViewModel window = new(offer.Start.ActivityKind, _services);
            // The pilot first: joining creates this member's own run row, and that row is filed under whoever this
            // window is for.
            if (pilot is { EsiCharacterId: { } characterId })
                window.UseCharacter(characterId, pilot.Name);
            // Every other toon picked alongside the pilot (ET-210) joins on the same group code, each with its own
            // row — the window still shows one, but the store gets one per character.
            if (additional is { Count: > 0 })
                window.UseAdditionalCharacters(
                    [.. additional.Select(character => (character.EsiCharacterId!.Value, character.Name))]);
            window.JoinFleetRun(offer.Start);
            _dialogs.ShowActivityWindow(window, RunWindowOpenTrigger.RemoteFleetCommander);
        });
    }

    /// <summary>What the commander announced, and whether it was only prepared rather than started (ET-246).</summary>
    private sealed record Offer(RunGroupCodeStart Start, bool IsPrepared);

    private static string _Where(RunGroupCodeStart start) =>
        string.Join(" · ", new[] { _SiteOf(start), start.SolarSystemName }
            .Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } named
            ? named
            : "Site and system not known";

    // A pocket has no site name, so its filament stands in the site's place — "Fierce Dark · Osmon" (ET-246).
    private static string? _SiteOf(RunGroupCodeStart start) => start.SiteName
        ?? (start.AbyssalTierIndex is not null
            ? AbyssalFilamentName.From(start.AbyssalTierIndex, start.AbyssalWeatherName)
            : null);
}
