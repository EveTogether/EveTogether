namespace EveUtils.Client.LocalApi;

/// <summary>
/// Response DTO for <c>GET /api/v1/health</c> — the liveness probe widget authors hit to confirm the local API
/// is up and which contract version it speaks. Serialized as camelCase JSON. Versioned, public DTO: it never
/// exposes internal entities.
/// </summary>
public sealed record HealthResponse(string Status, string AppVersion, string ApiVersion);
