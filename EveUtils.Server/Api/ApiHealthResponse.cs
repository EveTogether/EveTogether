namespace EveUtils.Server.Api;

/// <summary>
/// Response DTO for <c>GET /api/v1/health</c>. Mirrors the local API's health shape so a consumer of both
/// reads one contract.
/// </summary>
public sealed record ApiHealthResponse(string Status, string AppVersion, string ApiVersion);
