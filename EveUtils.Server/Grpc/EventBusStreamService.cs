using System.IO;
using System.Reflection;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Messaging;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Grpc;

/// <summary>
/// The server side of the remote event bus. Attach is auth-gated: only a valid server
/// session token (gRPC bearer metadata) may connect. Inbound events are published on the server's
/// LOCAL bus (server handlers + the SignalR bridge subscribe) and rerouted to the other connected
/// clients — the local bus is the universal delivery point on every host.
/// </summary>
public sealed class EventBusStreamService(
    ServerSessionService sessions,
    IEventTypeRegistry registry,
    ConnectedClients connectedClients,
    IServiceProvider services) : EventBusStream.EventBusStreamBase
{
    public override async Task Attach(
        IAsyncStreamReader<ClientEnvelope> requestStream,
        IServerStreamWriter<ServerEnvelope> responseStream,
        ServerCallContext context)
    {
        var token = ExtractBearer(context);
        var session = token is null ? null : await sessions.ValidateAsync(token, context.CancellationToken);
        if (session is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A valid session token is required to attach the event bus."));

        var key = TokenSecurity.Hash(token!);
        var characterName = session.SyncedCharacter?.CharacterName ?? "unknown";
        var attachedCharacterId = session.SyncedCharacter?.EsiCharacterId ?? 0;

        // Durable delivery on attach: re-send the character's still-pending queued messages (mail +
        // invites, the single inbox channel). Done BEFORE registering the connection so this write can't race a
        // concurrent broadcast on the shared stream writer.
        if (attachedCharacterId != 0)
            await services.GetRequiredService<MessageDeliveryService>()
                .DeliverPendingAsync(responseStream, attachedCharacterId, context.CancellationToken);

        connectedClients.Add(new ConnectedClient(key, attachedCharacterId, characterName, responseStream));
        try
        {
            await foreach (var envelope in requestStream.ReadAllAsync(context.CancellationToken))
            {
                // Server-authoritative source attribution (SEC): an event over the bus is always attributed to the
                // attached, validated session character — never the client-supplied CharacterId, which would let an
                // authenticated client publish events on another character's behalf. Reject an explicit mismatch.
                if (envelope.Event.CharacterId != 0 && envelope.Event.CharacterId != attachedCharacterId)
                {
                    services.GetRequiredService<ILogger<EventBusStreamService>>().LogWarning(
                        "Event bus message rejected: session for character {Attached} published as character {Claimed}.",
                        attachedCharacterId, envelope.Event.CharacterId);
                    continue;
                }

                var characterId = attachedCharacterId == 0 ? (int?)null : attachedCharacterId;
                var evt = registry.Deserialize(envelope.Event.EventType, envelope.Event.PayloadJson, characterId);
                if (evt is null)
                    continue;

                // Event-bus permission gate: a remote event may declare [RequiresPermission];
                // check it BEFORE both local delivery and reroute, so a disabled permission (e.g.
                // fit.sync off) blocks the whole path — not just storage.
                if (!await IsEventAllowedAsync(evt, context.CancellationToken))
                    continue;

                // Server local bus: server handlers + the SignalR bridge pick it up.
                var bus = services.GetRequiredService<IEventBus>();
                await bus.PublishAsync(evt, EventTarget.Local, context.CancellationToken);

                // Reroute strategy: a targeted event goes only to that character's connections;
                // a fleet-scoped event goes to the fleet's live broadcast set — its roster members who are connected
                // (server-authoritative: membership ∩ presence), excluding the sender; anything else broadcasts.
                if (envelope.Event.TargetCharacterId != 0)
                    await connectedClients.SendToCharacterAsync(envelope.Event.TargetCharacterId, envelope.Event, context.CancellationToken);
                else if (envelope.Event.FleetId != 0)
                {
                    using var scope = services.CreateScope();
                    var broadcast = scope.ServiceProvider.GetRequiredService<FleetBroadcastResolver>();

                    // Send-side authorization (SEC): only an actual roster member may reroute events into a fleet's
                    // broadcast stream. Delivery below is already member∩presence, but without this gate a non-member
                    // could inject events (e.g. a forged fleet.changed) to every member of a fleet he is not in.
                    if (attachedCharacterId == 0 ||
                        !await broadcast.IsMemberAsync(envelope.Event.FleetId, attachedCharacterId, context.CancellationToken))
                        continue;

                    // Live fleet traffic (the participation metric stream) keeps the fleet's cleanup clock fresh so an
                    // actively-playing fleet is not archived the moment everyone briefly disconnects.
                    // Throttled to ~1/min per fleet, so combat — which never hits a roster command — counts as activity.
                    await services.GetRequiredService<FleetActivityTracker>()
                        .NoteAsync(envelope.Event.FleetId, DateTimeOffset.UtcNow, context.CancellationToken);

                    // Only an ACTIVE fleet broadcasts — a Forming fleet (advance sign-up) delivers nothing even if
                    // an old/buggy client publishes to it.
                    var members = await broadcast.ActiveBroadcastMembersAsync(envelope.Event.FleetId, context.CancellationToken);
                    await connectedClients.SendToCharactersAsync(members, envelope.Event, context.CancellationToken, exceptKey: key);
                }
                else
                    await connectedClients.BroadcastExceptAsync(key, envelope.Event, context.CancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // A client closing its stream aborts the read — that is a normal disconnect, not an error. Swallow it so
            // gRPC does not log the call as a faulted/cancelled method; the finally still removes the connection.
            services.GetRequiredService<ILogger<EventBusStreamService>>()
                .LogDebug("Event bus client {Character} disconnected.", characterName);
        }
        finally
        {
            connectedClients.Remove(key);
        }
    }

    /// <summary>
    /// Checks the app-permission an event declares via <see cref="RequiresPermissionAttribute"/> against
    /// the server's <see cref="IAccessPolicy"/> (e.g. the fit.sync toggle). No attribute = always allowed.
    /// </summary>
    private async Task<bool> IsEventAllowedAsync(IIntegrationEvent evt, CancellationToken cancellationToken)
    {
        var code = evt.GetType().GetCustomAttribute<RequiresPermissionAttribute>()?.Code;
        if (code is null) return true;

        var policy = services.GetRequiredService<IAccessPolicy>();
        var principals = services.GetRequiredService<IPrincipalAccessor>();
        var allowed = await policy.IsAllowedAsync(principals.Current, code, cancellationToken);
        if (!allowed)
        {
            services.GetRequiredService<ILogger<EventBusStreamService>>().LogError(
                "Remote event {EventType} blocked: permission '{Code}' denied (PERMISSION_DENIED).",
                evt.EventType, code);
        }
        return allowed;
    }

    private static string? ExtractBearer(ServerCallContext context)
    {
        var authorization = context.RequestHeaders.GetValue("authorization");
        return authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..]
            : null;
    }
}
