namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// EVE SSO app configuration. The client uses only <see cref="ClientId"/> (PKCE public); the
/// server additionally holds <see cref="ClientSecret"/> for the confidential exchange. The
/// secret must never ship in the open-source client — server config only.
/// </summary>
public sealed class EsiOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string? ClientSecret { get; init; }
    public string CallbackUri { get; init; } = "http://127.0.0.1:7345/callback";
    public string[] Scopes { get; init; } = [];
}
