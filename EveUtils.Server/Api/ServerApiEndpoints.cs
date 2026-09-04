using System.Security.Claims;
using EveUtils.Server.Api.Dtos;
using EveUtils.Shared.App;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;

namespace EveUtils.Server.Api;

/// <summary>
/// The read-only REST API for external consumers. Registered from here rather than inline in the host so the gate
/// on the group is one reviewable — and testable — line. Everything under <c>/api/v1</c> is behind the API key;
/// <c>/health</c>, <c>/openapi/v1.json</c> and <c>/scalar</c> are the ratified exceptions (decision 4).
/// </summary>
public static class ServerApiEndpoints
{
    public const string ApiVersion = "v1";

    public static void MapServerApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi();            // GET /openapi/v1.json — keyless
        endpoints.MapScalarApiReference(); // interactive reference at /scalar — keyless

        RouteGroupBuilder api = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(ApiKeyAuthentication.Policy)
            .RequireRateLimiting(ServerApiHardening.RateLimitPolicy)
            .RequireCors(ServerApiHardening.CorsPolicy);

        // Public liveness probe: a consumer must be able to see the server is up before it has a key — and, for
        // the same reason, without a key's limit standing in the way.
        api.MapGet("/health", () => new ApiHealthResponse("ok", AppInfo.Version, ApiVersion))
            .AllowAnonymous()
            .DisableRateLimiting();

        api.MapGet("/whoami", (ClaimsPrincipal user) => Results.Ok(ApiWhoAmIResponse.From(user)));

        // Inside the group, so the realtime channel is admitted by the same key policy as every route here rather
        // than by an authorisation path of its own. A browser cannot put a header on a WebSocket handshake, and
        // the ratified ?apikey= form (decision 7) already covers that.
        api.MapHub<ServerApiRealtimeHub>("/realtime");

        // [FromServices] rather than inference: the bridge is a plain class, and an unregistered one would
        // otherwise be read as a request body instead of failing to resolve.
        api.MapGet("/fleets", (ClaimsPrincipal user, [FromServices] ServerApiQueries queries, CancellationToken ct) =>
            queries.GetFleetsAsync(user.OwnerCharacterId(), ct));
        api.MapGet("/fleets/{id:long}", async (long id, ClaimsPrincipal user, [FromServices] ServerApiQueries queries,
            CancellationToken ct) =>
            await queries.GetFleetAsync(id, user.OwnerCharacterId(), ct) is { } fleet
                ? Results.Ok(fleet)
                : Results.NotFound());

        api.MapGet("/compositions", ([FromServices] ServerApiQueries queries, CancellationToken ct) =>
            queries.GetCompositionsAsync(ct));
        api.MapGet("/compositions/{id:long}", async (long id, [FromServices] ServerApiQueries queries, CancellationToken ct) =>
            await queries.GetCompositionAsync(id, ct) is { } composition
                ? Results.Ok(composition)
                : Results.NotFound());

        api.MapGet("/fits", ([FromServices] ServerApiQueries queries, CancellationToken ct) =>
            queries.GetFitsAsync(ct));
        api.MapGet("/fits/{id:int}", async (int id, [FromServices] ServerApiQueries queries, CancellationToken ct) =>
            await queries.GetFitAsync(id, ct) is { } fit
                ? Results.Ok(fit)
                : Results.NotFound());

        api.MapGet("/characters", (ClaimsPrincipal user, [FromServices] ServerApiQueries queries, CancellationToken ct) =>
            queries.GetCharactersAsync(user.OwnerCharacterId(), ct));
        api.MapGet("/characters/{id:int}", async (int id, ClaimsPrincipal user,
            [FromServices] ServerApiQueries queries, CancellationToken ct) =>
            await queries.GetCharacterAsync(id, user.OwnerCharacterId(), ct) is { } character
                ? Results.Ok(character)
                : Results.NotFound());

        api.MapGet("/metrics", (ClaimsPrincipal user, [FromServices] ServerApiQueries queries, CancellationToken ct) =>
            queries.GetMetricsAsync(user.OwnerCharacterId(), ct));
    }
}
