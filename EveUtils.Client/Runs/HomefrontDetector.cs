using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Runs;

/// <summary>
/// Recognises a homefront from what the gamelog shows of it and offers its run with one click (ET-348). Only the
/// hallmark types in <see cref="HomefrontSignatures"/> name a site. Seeing one does not prove the pilot is inside
/// that site (another grid, a fleet mate's target), so this asks instead of starting: two sightings close together,
/// an own character in a fleet, and no run of theirs already going.
///
/// One offer per sighting of a site per fleet. It is not asked again after Ignore, only once that site has gone
/// quiet (<see cref="QuietGap"/>) or a run on it has ended (<see cref="RunEndedGrace"/>). A fleet flying the same
/// site kind again later gets a new question.
/// </summary>
public sealed class HomefrontDetector : ISingletonService, IDisposable
{
    /// <summary>"false" turns the offer off. Default on: unset or "true" offers.</summary>
    public const string OfferSettingKey = "homefront.offer-from-enemies";

    // Offertory Sigils and dread cap cycles write a line every few seconds; two in a minute is a site being flown.
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMinutes(1);

    // Long enough to span a lull between hauler waves, short enough that the next site of the same kind is asked.
    private static readonly TimeSpan QuietGap = TimeSpan.FromMinutes(5);

    // The same idea as ClipboardSignatureOffer's duplicate window, sized for combat instead of a clipboard event:
    // the last shots at a site's objects can land after SAVE, and those must not ask to run it again.
    private static readonly TimeSpan RunEndedGrace = TimeSpan.FromMinutes(2);

    private readonly GamelogClientService _gamelog;
    private readonly ISdeAccessor _sde;
    private readonly IToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IDisposable[] _subscriptions;
    private readonly Lock _gate = new();

    private readonly ConcurrentDictionary<string, int?> _dungeonIdByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<int>> _dungeonIdsBySiteName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, (int CharacterId, string? SiteName)> _runs = [];
    private readonly List<(string SiteName, DateTimeOffset EndedAtUtc)> _endedRuns = [];
    private readonly Dictionary<(long FleetId, int DungeonId), Sighting> _sightings = [];
    private IReadOnlyDictionary<int, SdeSite>? _homefrontSites;

    public HomefrontDetector(GamelogClientService gamelog, IEventBus eventBus, ISdeAccessor sde, IToastService toasts,
        IDialogService dialogs, IServiceProvider services)
    {
        _gamelog = gamelog;
        _sde = sde;
        _toasts = toasts;
        _dialogs = dialogs;
        _services = services;
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;

        _gamelog.CombatObserved += _OnObserved;
        _gamelog.CounterpartyObserved += _OnObserved;
        _subscriptions =
        [
            eventBus.Subscribe<RunStartedEvent>(evt => _OnRunStarted(evt.Data)),
            eventBus.Subscribe<RunSavedEvent>(evt => _OnRunEnded(evt.Data)),
            eventBus.Subscribe<RunDeletedEvent>(evt => _OnRunEnded(evt.Data))
        ];
    }

    public void Dispose()
    {
        _gamelog.CombatObserved -= _OnObserved;
        _gamelog.CounterpartyObserved -= _OnObserved;
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();
    }

    private void _OnRunStarted(RunStartedEventData run)
    {
        lock (_gate)
            _runs[run.RunId] = (checked((int)run.CharacterId), run.SiteName);
    }

    private void _OnRunEnded(Guid runId)
    {
        lock (_gate)
        {
            if (!_runs.Remove(runId, out var run) || run.SiteName is null)
                return;

            _endedRuns.RemoveAll(ended => _clock.GetUtcNow() - ended.EndedAtUtc >= RunEndedGrace);
            _endedRuns.Add((run.SiteName, _clock.GetUtcNow()));
        }
    }

    // Raised on the gamelog pump, never the UI thread, so the SDE lookups below are allowed here (ET-298).
    private void _OnObserved(int characterId, string name, DateTime observedAtUtc, DamageDirection direction)
    {
        if (_DungeonIdOf(name) is not { } dungeonId)
            return;

        IReadOnlyList<FleetParticipant> participation = _services.GetService<IFleetParticipation>()?.Current ?? [];
        if (OwnCharacterPickMemory.FleetIdFor(characterId, participation) is not { } fleetId)
            return;

        if (_ShouldOffer(characterId, fleetId, dungeonId, observedAtUtc) && _HomefrontSite(dungeonId) is { } site)
            _ = _OfferAsync(site, name, characterId, fleetId);
    }

    private async Task _OfferAsync(SdeSite site, string seen, int characterId, long fleetId)
    {
        try
        {
            if (!await _IsEnabledAsync())
                return;

            Dispatcher.UIThread.Post(() => _toasts.Show($"Start Homefront run: {site.Name}?", $"{seen} seen in the game log.",
                ToastKind.Information,
                [
                    new ToastAction("Ignore", () => { }),
                    new ToastAction("Start", () => _ = _StartRunAsync(site, characterId, fleetId), ToastActionStyle.Affirmative)
                ], onClosed: null, replacementKey: $"homefront-offer:{fleetId}:{site.DungeonId}"));
        }
        catch (Exception ex)
        {
            // A settings read that fails costs this one offer, never the gamelog pump that raised it.
            _services.GetService<ILoggerFactory>()?.CreateLogger<HomefrontDetector>()
                .LogWarning(ex, "Could not offer a homefront run on {Site}.", site.Name);
        }
    }

    private bool _ShouldOffer(int characterId, long fleetId, int dungeonId, DateTime observedAtUtc)
    {
        lock (_gate)
        {
            if (_runs.Values.Any(run => run.CharacterId == characterId) || _RunEndedRecentlyOn(dungeonId))
                return false;

            if (!_sightings.TryGetValue((fleetId, dungeonId), out Sighting? sighting)
                || _Apart(observedAtUtc, sighting.LastAtUtc) > QuietGap)
            {
                _sightings[(fleetId, dungeonId)] = new Sighting(observedAtUtc);
                return false;
            }

            bool closeTogether = _Apart(observedAtUtc, sighting.LastAtUtc) <= DebounceWindow;
            if (observedAtUtc > sighting.LastAtUtc)
                sighting.LastAtUtc = observedAtUtc;
            if (sighting.Offered || !closeTogether)
                return false;

            sighting.Offered = true;
            return true;
        }
    }

    // Called under _gate. A run's site is kept as the name it was started on — possibly a translated one copied from
    // the scan window — so it is matched to dungeon ids the same way the clipboard does.
    private bool _RunEndedRecentlyOn(int dungeonId)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (var (siteName, endedAtUtc) in _endedRuns)
        {
            if (now - endedAtUtc >= RunEndedGrace)
                continue;

            if (!_dungeonIdsBySiteName.GetOrAdd(siteName, name => [.. _sde.FindSitesByExactName(name).Select(s => s.DungeonId)])
                    .Contains(dungeonId))
                continue;

            // The site is done: whatever is seen of it after the grace is a new site of the same kind.
            foreach ((long FleetId, int DungeonId) key in _sightings.Keys.Where(key => key.DungeonId == dungeonId).ToList())
                _sightings.Remove(key);
            return true;
        }

        return false;
    }

    private static TimeSpan _Apart(DateTime first, DateTime second) => (first - second).Duration();

    // The gamelog writes a bare type name in the client's language; every type carrying that name is a candidate,
    // and only one in the table counts, so a same-named type in another group can never stand in for it.
    private int? _DungeonIdOf(string name) =>
        _dungeonIdByName.GetOrAdd(name, typeName =>
            _sde.FindTypeIdsByName(typeName)
                .Select(typeId => HomefrontSignatures.DungeonIdByTypeId.TryGetValue(typeId, out int dungeonId) ? dungeonId : (int?)null)
                .FirstOrDefault(dungeonId => dungeonId is not null));

    private SdeSite? _HomefrontSite(int dungeonId)
    {
        _homefrontSites ??= _sde.SearchSites()
            .Where(site => HomefrontCatalogue.IsHomefrontDungeonId(site.DungeonId))
            .DistinctBy(site => site.DungeonId)
            .ToDictionary(site => site.DungeonId);
        return _homefrontSites.GetValueOrDefault(dungeonId);
    }

    private async Task<bool> _IsEnabledAsync()
    {
        if (_services.GetService<ISettingRepository>() is not { } settings)
            return true;

        foreach (ClientSetting setting in await settings.ListAsync())
            if (setting.Key == OfferSettingKey)
                return !string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// The same window a copied homefront opens (<c>ClipboardSignatureOffer._StartRunAsync</c>), with the site
    /// already known, so nobody is asked which site it is. Nobody is asked whose run it is either: the character who
    /// saw it flies it, and this client's other characters in the same fleet ride along, minus the ones this fleet
    /// left out last time (ET-270). Changing that is a correction made in the window afterwards, not a question now.
    /// </summary>
    private async Task _StartRunAsync(SdeSite site, int characterId, long fleetId)
    {
        try
        {
            List<Character> known = _services.GetService<ICharacterRegistry>() is { } registry
                ? [.. (await registry.GetAllAsync()).Where(character => character.EsiCharacterId is not null)]
                : [];
            if (known.FirstOrDefault(character => character.EsiCharacterId == characterId) is not { } pilot)
                return;

            IReadOnlyList<FleetParticipant> participation = _services.GetService<IFleetParticipation>()?.Current ?? [];
            IReadOnlyCollection<int>? fleetCharacterIds = OwnCharacterPickMemory.FleetCharacterIdsFor(characterId, participation);
            List<int> flying = [.. InGameCharacters.Among(known, _services.GetService<ILocalCharacterPresence>())
                .Select(character => character.EsiCharacterId).OfType<int>()];
            // Seeing no client at all is not knowing (same rule as the clipboard's own question): the fleet stands in.
            IReadOnlyCollection<int> available = flying.Count == 0 ? fleetCharacterIds ?? [] : flying;
            IReadOnlyList<int> picked = await OwnCharacterPickMemory.ResolvePreselectionAsync(
                _services.GetService<ISettingRepository>(), characterId, available, fleetCharacterIds, fleetId) ?? [characterId];

            var window = new ActivityWindowViewModel(ActivityKind.Site, _services)
            {
                SignatureName = site.Name,
                MatchedSites = [site],
                StartsOnArrival = true
            };
            window.UseCharacter(characterId, pilot.Name);
            List<(int, string)> additional = [];
            foreach (int id in picked)
                if (id != characterId && known.FirstOrDefault(character => character.EsiCharacterId == id) is { } rider)
                    additional.Add((id, rider.Name));
            if (additional.Count > 0)
                window.UseAdditionalCharacters(additional);

            _dialogs.ShowActivityWindow(window, RunWindowOpenTrigger.LocalUser);
        }
        catch (Exception ex)
        {
            // The only caller is a toast button returning void, so an escape here is an unobserved task.
            _toasts.Show("Run not started", $"Could not open the run on {site.Name}: {ex.Message}", ToastKind.Error);
        }
    }

    private sealed class Sighting(DateTime firstAtUtc)
    {
        public DateTime LastAtUtc { get; set; } = firstAtUtc;

        public bool Offered { get; set; }
    }
}
