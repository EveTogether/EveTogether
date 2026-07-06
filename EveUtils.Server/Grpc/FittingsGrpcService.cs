using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using GrpcFittings = EveUtils.Grpc.Fittings;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Synchronous fit-sharing. Auth-gated by the server session token; enforces the
/// <c>fit.sync</c> app-permission SERVER-SIDE and returns a real accept/deny result so the client
/// can show the truth (a fire-and-forget event gave false "shared" feedback). On accept it stores the
/// fit and reroutes it to the other connected clients over the event bus.
/// </summary>
public sealed class FittingsGrpcService(
    ServerSessionService sessions,
    ISharedFitRepository repository,
    IAccessPolicy policy,
    IPrincipalAccessor principals,
    ConnectedClients connectedClients,
    ILogger<FittingsGrpcService> logger) : GrpcFittings.FittingsBase
{
    public override async Task<ShareFitReply> ShareFit(ShareFitRequest request, ServerCallContext context)
    {
        var token = ExtractBearer(context);
        var session = token is null ? null : await sessions.ValidateAsync(token, context.CancellationToken);
        if (session is null)
            return new ShareFitReply { Accepted = false, Message = "Not authenticated — pair with the server first." };

        // Attribution comes from the validated session, never the request body (SEC): otherwise an authenticated
        // client could share a fit on another character's behalf.
        var sharedByCharacterId = session.SyncedCharacter?.EsiCharacterId ?? 0;
        var sharedByCharacterName = session.SyncedCharacter?.CharacterName ?? "unknown";

        // App-permission gate (two-layer, app side): fit.sync must be enabled on the server.
        if (!await policy.IsAllowedAsync(principals.Current, FittingsPermissions.Sync, context.CancellationToken))
        {
            logger.LogError("Share of '{Name}' from {Char} rejected: fit.sync disabled (PERMISSION_DENIED).",
                request.Name, sharedByCharacterName);
            return new ShareFitReply { Accepted = false, Message = "fit.sync is disabled on the server." };
        }

        var match = await repository.AddOrMatchAsync(new SharedFit
        {
            EsiFittingId = request.EsiFittingId,
            Name = request.Name,
            ShipTypeId = request.ShipTypeId,
            RawJson = request.RawJson,
            SharedByCharacterName = sharedByCharacterName,
            SharedByCharacterId = sharedByCharacterId,
            SharedAt = DateTimeOffset.UtcNow
        }, context.CancellationToken);

        // Content-hash dedup (2026-06-04): an identical fit is already in the library — don't add a second row or
        // reroute a "new fit" event; report which fit it matched so the user knows why nothing changed.
        if (match is not null)
        {
            logger.LogInformation("Skipped duplicate share '{Name}' from {Char}: same content as '{Existing}' (id {Id}).",
                request.Name, sharedByCharacterName, match.Name, match.Id);
            return new ShareFitReply
            {
                Accepted = true,
                Message = $"Already shared as '{match.Name}' — not added again (same fit)."
            };
        }

        // Reroute to connected clients so they see the new shared fit. Reuse the wire event.
        var payload = new FitSharedPayload(request.EsiFittingId, request.Name, request.ShipTypeId,
            request.RawJson, sharedByCharacterName);
        var envelope = new EventEnvelope
        {
            EventType = "fittings.shared",
            EventId = Guid.NewGuid().ToString(),
            CharacterId = sharedByCharacterId,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload)
        };
        await connectedClients.BroadcastExceptAsync("", envelope, context.CancellationToken);

        logger.LogInformation("Stored + rerouted shared fit '{Name}' from {Char}.", request.Name, sharedByCharacterName);
        return new ShareFitReply { Accepted = true, Message = "Shared." };
    }

    public override async Task<GetSharedFitsReply> GetSharedFits(GetSharedFitsRequest request, ServerCallContext context)
    {
        var token = ExtractBearer(context);
        var session = token is null ? null : await sessions.ValidateAsync(token, context.CancellationToken);
        if (session is null)
            return new GetSharedFitsReply { Ok = false, Message = "Not authenticated — pair with the server first." };

        var reply = new GetSharedFitsReply { Ok = true, Message = "" };
        foreach (var fit in await repository.ListAsync(context.CancellationToken))
        {
            reply.Fits.Add(new SharedFitDto
            {
                Id = fit.Id,
                EsiFittingId = fit.EsiFittingId,
                Name = fit.Name,
                ShipTypeId = fit.ShipTypeId,
                RawJson = fit.RawJson,
                SharedByCharacterName = fit.SharedByCharacterName,
                SharedByCharacterId = fit.SharedByCharacterId,
                SharedAt = fit.SharedAt.ToString("o")
            });
        }
        return reply;
    }

    public override async Task<DeleteSharedFitReply> DeleteSharedFit(DeleteSharedFitRequest request, ServerCallContext context)
    {
        var token = ExtractBearer(context);
        var session = token is null ? null : await sessions.ValidateAsync(token, context.CancellationToken);
        if (session is null)
            return new DeleteSharedFitReply { Accepted = false, Message = "Not authenticated — pair with the server first." };

        // Needs the fit.manage permission — separate from fit.sync.
        if (!await policy.IsAllowedAsync(principals.Current, FittingsPermissions.Manage, context.CancellationToken))
        {
            logger.LogError("Delete of shared fit {Id} rejected: fit.manage denied (PERMISSION_DENIED).", request.Id);
            return new DeleteSharedFitReply { Accepted = false, Message = "You don't have rights to manage the server library (fit.manage)." };
        }

        var removed = await repository.RemoveAsync(request.Id, context.CancellationToken);
        return removed
            ? new DeleteSharedFitReply { Accepted = true, Message = "Deleted." }
            : new DeleteSharedFitReply { Accepted = false, Message = "Fit not found on the server." };
    }

    private static string? ExtractBearer(ServerCallContext context)
    {
        var authorization = context.RequestHeaders.GetValue("authorization");
        return authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..]
            : null;
    }
}
