using EveUtils.Client.Killmails;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Stores what fleet mates share with <c>fleet.killmail-share</c> (ET-371) under their own character, kept across
/// restarts. Per (fleet, sender) only the newest share counts; an older one arriving late is ignored. The server echo
/// of an own share is ignored too, so an own empty share can never remove an own killmail. The bus callback only
/// queues the work, so delivery never waits on ESI or the database.
/// </summary>
public sealed class FleetKillmailShareReceiver : ISingletonService, IDisposable
{
    private readonly EsiKillmailImporter _importer;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FleetKillmailShareReceiver> _logger;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly Dictionary<(long FleetId, int CharacterId), long> _newestUnixMs = [];
    private Task _work = Task.CompletedTask;

    public FleetKillmailShareReceiver(
        IEventBus eventBus,
        EsiKillmailImporter importer,
        IServiceScopeFactory scopes,
        ILogger<FleetKillmailShareReceiver> logger)
    {
        _importer = importer;
        _scopes = scopes;
        _logger = logger;
        _subscription = eventBus.Subscribe<FleetKillmailShareEvent>(_OnShareAsync);
    }

    /// <summary>Completes once every share received so far has been handled.</summary>
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

    public void Dispose() => _subscription.Dispose();

    private bool _IsLast(Task work)
    {
        lock (_gate)
        {
            return ReferenceEquals(work, _work);
        }
    }

    private Task _OnShareAsync(FleetKillmailShareEvent share, CancellationToken cancellationToken)
    {
        if (share.CharacterId is not { } sender)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            _work = _work.ContinueWith(
                _ => _RunAsync(sender, share),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }

        return Task.CompletedTask;
    }

    private async Task _RunAsync(int sender, FleetKillmailShareEvent share)
    {
        try
        {
            await _HandleAsync(sender, share);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Storing a fleet mate's shared killmails failed");
        }
    }

    private async Task _HandleAsync(int sender, FleetKillmailShareEvent share)
    {
        var key = (share.FleetId, sender);
        if (_newestUnixMs.TryGetValue(key, out long newest) && share.Data.UnixMs < newest)
        {
            return;
        }

        _newestUnixMs[key] = share.Data.UnixMs;

        await using (AsyncServiceScope scope = _scopes.CreateAsyncScope())
        {
            IReadOnlyList<Character> characters =
                await scope.ServiceProvider.GetRequiredService<ICharacterRegistry>().GetAllAsync(CancellationToken.None);
            if (characters.Any(character => character.EsiCharacterId == sender))
            {
                return;
            }
        }

        KillmailImportResult result = await _importer.ImportFleetShareAsync(sender, share.FleetId,
            [.. share.Data.Killmails.Select(killmail => (killmail.KillmailId, killmail.Hash))]);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Shared killmails of {CharacterId} in fleet {FleetId} were not stored: {Message}",
                sender, share.FleetId, result.Message);
        }
    }
}
