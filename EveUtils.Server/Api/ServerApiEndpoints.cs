using System.Security.Claims;
using EveUtils.Server.Api.Dtos;
using EveUtils.Shared.App;
using Microsoft.AspNetCore.Mvc;
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

        RouteGroupBuilder api = endpoints.MapGroup("/api/v1").RequireAuthorization(ApiKeyAuthentication.Policy);

        // Public liveness probe: a consumer must be able to see the server is up before it has a key.
        api.MapGet("/health", () => new ApiHealthResponse("ok", AppInfo.Version, ApiVersion)).AllowAnonymous();

        api.MapGet("/whoami", (ClaimsPrincipal user) => Results.Ok(ApiWhoAmIResponse.From(user)));

        // [FromServices] rather than inference: the bridge is a plain class, and an unregistered one would
        // otherwise be read as a request body instead of failing to resolve.
        api.MapGet("/fleets", ([FromServices] ServerApiQueries queries, CancellationToken ct) =>
            queries.GetFleetsAsync(ct));
        api.MapGet("/fleets/{id:long}", async (long id, [FromServices] ServerApiQueries queries, CancellationToken ct) =>
            await queries.GetFleetAsync(id, ct) is { } fleet ? Results.Ok(fleet) : Results.NotFound());

        api.MapGet("/compositions", ([FromServices] ServerApiQueries queries, CancellationToken ct) =>
            queries.GetCompositionsAsync(ct));
        api.MapGet("/compositions/{id:long}", async (long id, [FromServices] ServerApiQueries queries, CancellationToken ct) =>
            await queries.GetCompositionAsync(id, ct) is { } composition
                ? Results.Ok(composition)
                : Results.NotFound());
    }
}
