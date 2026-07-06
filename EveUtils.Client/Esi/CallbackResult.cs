namespace EveUtils.Client.Esi;

/// <summary>The query parameters EVE SSO hands back to the loopback callback.</summary>
public sealed record CallbackResult(string? Code, string? State, string? Error);
