using System.Security.Claims;

namespace EveUtils.Server.Api;

/// <summary>
/// The read-only REST API for external consumers. M0 maps the lock and one endpoint that does nothing but prove
/// the key works (ET-118); the data routes and the public <c>/health</c> + self-docs land in M1. Registered from
/// here rather than inline in the host so the gate on the group is one reviewable — and testable — line.
/// </summary>
public static class ServerApiEndpoints
{
    public static void MapServerApi(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1").RequireAuthorization(ApiKeyAuthentication.Policy);
        api.MapGet("/whoami", (ClaimsPrincipal user) => Results.Ok(ApiWhoAmIResponse.From(user)));
    }
}
