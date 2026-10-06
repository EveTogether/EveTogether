using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Publishes a character's complete killmail state for every active fleet participation after a real killmail import.
/// The event callback only queues work so a store command never waits on repository reads or remote delivery.
/// </summary>
public sealed class FleetKillmailSharePublisher : ISingletonService, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IFleetParticipation _participation;
    private readonly ILocalKillmailReader _killmails;
    private readonly IMetricShareSettings _shareSettings;
    private readonly TimeProvider _clock;
    private readonly ILogger<FleetKillmailSharePublisher> _logger;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private Task _work = Task.CompletedTask;

    public FleetKillmailSharePublisher(
        IEventBus eventBus,
        IFleetParticipation participation,
        ILocalKillmailReader killmails,
        IMetricShareSettings shareSettings,
        TimeProvider clock,
        ILogger<FleetKillmailSharePublisher> logger)
    {
        _eventBus = eventBus;
        _participation = participation;
        _killmails = killmails;
        _shareSettings = shareSettings;
        _clock = clock;
        _logger = logger;
        _subscription = eventBus.Subscribe<KillmailsChangedEvent>(_OnKillmailsChangedAsync);
    }

    public async Task WhenIdleAsync()
    {
        Task current;
        do
        {
            lock (_gate)
            {
                current = _work;
            }

            await current;
        } while (!_IsLast(current));
    }

    public Task PublishCurrentAsync(string? serverAddress = null, long? fleetId = null) =>
        _Enqueue(cancellationToken => _PublishCurrentAsync(serverAddress, fleetId, cancellationToken));

    public void Dispose() => _subscription.Dispose();

    private bool _IsLast(Task work)
    {
        lock (_gate)
        {
            return ReferenceEquals(work, _work);
        }
    }

    private Task _OnKillmailsChangedAsync(KillmailsChangedEvent changed, CancellationToken cancellationToken)
    {
        if (changed.Data.Kind == KillmailsChangeKind.Imported)
        {
            _ = _Enqueue(token => _PublishAsync(changed.Data.CharacterId, null, null, token));
        }

        return Task.CompletedTask;
    }

    private Task _Enqueue(Func<CancellationToken, Task> work)
    {
        lock (_gate)
        {
            return _work = _work.ContinueWith(
                _ => _RunAsync(work),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task _RunAsync(Func<CancellationToken, Task> work)
    {
        try
        {
            await work(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Automatic fleet killmail sharing failed");
        }
    }

    private async Task _PublishCurrentAsync(string? serverAddress, long? fleetId, CancellationToken cancellationToken)
    {
        int[] characterIds = [.. _participation.Current
            .Where(participant => _MatchesScope(participant, serverAddress, fleetId))
            .Select(participant => participant.CharacterId)
            .Distinct()];
        foreach (int characterId in characterIds)
        {
            await _PublishAsync(characterId, serverAddress, fleetId, cancellationToken);
        }
    }

    private async Task _PublishAsync(
        int characterId,
        string? serverAddress,
        long? fleetId,
        CancellationToken cancellationToken)
    {
        FleetParticipant[] participants = [.. _participation.Current.Where(entry =>
            entry.CharacterId == characterId && _MatchesScope(entry, serverAddress, fleetId))];
        if (participants.Length == 0)
        {
            return;
        }

        IReadOnlyList<LocalKillmail> stored = await _killmails.GetForCharacterAsync(characterId, cancellationToken);
        MetricShareSnapshot settings = await _shareSettings.LoadAsync(cancellationToken);
        long unixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        foreach (FleetParticipant participant in participants)
        {
            IReadOnlyList<FleetKillmailReference> shared =
                settings.IsKillmailShared(participant.ServerAddress, participant.FleetId, characterId)
                && participant.ActivatedAt is { } activatedAt
                    ? [.. stored
                        .Where(killmail => killmail.KillmailTimeUtc >= activatedAt.UtcDateTime)
                        .Select(killmail => new FleetKillmailReference
                        {
                            KillmailId = killmail.KillmailId,
                            Hash = killmail.Hash,
                            KillmailTimeUtc = killmail.KillmailTimeUtc,
                            IsLoss = killmail.IsLoss,
                        })]
                    : [];
            var payload = new FleetKillmailShare
            {
                FleetId = participant.FleetId,
                UnixMs = unixMs,
                Killmails = shared,
            };
            EventTarget target = participant.ClientOnly ? EventTarget.Local : EventTarget.Remote;
            await _eventBus.PublishAsync(new FleetKillmailShareEvent(payload, characterId), target, cancellationToken);
        }
    }

    private static bool _MatchesScope(FleetParticipant participant, string? serverAddress, long? fleetId) =>
        fleetId is null
        || participant.FleetId == fleetId
        && MetricShareSnapshot.KillmailOverrideKeyFor(participant.ServerAddress, participant.FleetId, participant.CharacterId)
           == MetricShareSnapshot.KillmailOverrideKeyFor(serverAddress, participant.FleetId, participant.CharacterId);
}
