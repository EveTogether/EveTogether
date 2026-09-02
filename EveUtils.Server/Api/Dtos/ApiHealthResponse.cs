namespace EveUtils.Server.Api.Dtos;

/// <summary>
/// Response for <c>GET /api/v1/health</c> — public and keyless (ratified decision 4), so a consumer can see the
/// server is up and which contract version it speaks before it has a key. Mirrors the Local API's health shape.
/// </summary>
public sealed record ApiHealthResponse(string Status, string AppVersion, string ApiVersion);
