using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using GrpcFittings = EveUtils.Grpc.Fittings;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Synchronous fit-sharing. Auth-gated by the server session token; enforces the
/// <c>fit.sync</c> app-permission SERVER-SIDE and returns a real accept/deny result so the client
/// can show the truth (a fire-and-forget event gave false "shared" feedback). Stores and deletes go through
/// <see cref="StoreSharedFitCommand"/> and <see cref="DeleteSharedFitCommand"/>, whose signal
/// <see cref="SharedFitChangeRelay"/> pushes to every connected client (ET-383). A refused session is the
/// exception: that answers <see cref="StatusCode.Unauthenticated"/>, not a reply payload — see
/// <see cref="AuthenticateAsync"/>.
/// </summary>
public sealed class FittingsGrpcService(
    ServerSessionService sessions,
    ISharedFitReader sharedFits,
    IDispatcher dispatcher,
    IAccessPolicy policy,
    IPrincipalAccessor principals,
    ILogger<FittingsGrpcService> logger) : GrpcFittings.FittingsBase
{
    public override async Task<ShareFitReply> ShareFit(ShareFitRequest request, ServerCallContext context)
    {
        var session = await AuthenticateAsync(context);

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

        var stored = await dispatcher.Send(new StoreSharedFitCommand(
            new FitSharedPayload(request.EsiFittingId, request.Name, request.ShipTypeId, request.RawJson, sharedByCharacterName),
            sharedByCharacterId), context.CancellationToken);
        if (!stored.IsSuccess)
            return new ShareFitReply { Accepted = false, Message = stored.Messages.FirstOrDefault()?.Text ?? "Sharing failed." };

        // Content-hash dedup (2026-06-04): an identical fit is already in the library, so nothing was added; the
        // message names the fit it matched so the user knows why nothing changed.
        if (stored.Value == 0)
        {
            logger.LogInformation("Skipped duplicate share '{Name}' from {Char}.", request.Name, sharedByCharacterName);
            return new ShareFitReply { Accepted = true, Message = stored.Messages.FirstOrDefault()?.Text ?? "" };
        }

        logger.LogInformation("Stored shared fit '{Name}' from {Char}.", request.Name, sharedByCharacterName);
        return new ShareFitReply { Accepted = true, Message = "Shared." };
    }

    public override async Task<GetSharedFitsReply> GetSharedFits(GetSharedFitsRequest request, ServerCallContext context)
    {
        await AuthenticateAsync(context);

        var reply = new GetSharedFitsReply { Ok = true, Message = "" };
        foreach (var fit in await sharedFits.ListAsync(context.CancellationToken))
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
        var session = await AuthenticateAsync(context);

        // Needs the fit.manage permission — separate from fit.sync.
        if (!await policy.IsAllowedAsync(principals.Current, FittingsPermissions.Manage, context.CancellationToken))
        {
            logger.LogError("Delete of shared fit {Id} rejected: fit.manage denied (PERMISSION_DENIED).", request.Id);
            return new DeleteSharedFitReply { Accepted = false, Message = "You don't have rights to manage the server library (fit.manage)." };
        }

        var deleted = await dispatcher.Send(
            new DeleteSharedFitCommand(request.Id, session.SyncedCharacter?.EsiCharacterId), context.CancellationToken);
        if (!deleted.IsSuccess)
            return new DeleteSharedFitReply { Accepted = false, Message = deleted.Messages.FirstOrDefault()?.Text ?? "Delete failed." };

        return new DeleteSharedFitReply { Accepted = true, Message = "Deleted." };
    }

    /// <summary>The validated session, or a <see cref="StatusCode.Unauthenticated"/> <see cref="RpcException"/> —
    /// the same signal <c>EventBusStreamService.Attach</c> already raises for a refused token. A status code rather
    /// than a reply payload so the client's refresh-and-retry can recover the call in flight instead of waiting for
    /// the next 30s heartbeat (ET-85, following ET-78).</summary>
    private async Task<ServerSession> AuthenticateAsync(ServerCallContext context)
    {
        var token = ExtractBearer(context);
        var session = token is null ? null : await sessions.ValidateAsync(token, context.CancellationToken);
        return session ?? throw new RpcException(new Status(StatusCode.Unauthenticated, NotAuthenticated));
    }

    private const string NotAuthenticated = "Not authenticated — pair with the server first.";

    private static string? ExtractBearer(ServerCallContext context)
    {
        var authorization = context.RequestHeaders.GetValue("authorization");
        return authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..]
            : null;
    }
}
