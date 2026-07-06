namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Outcome of a pre-flight auth check (<see cref="IEsiTokenProvider"/>). On
/// <see cref="EsiAuthOutcome.Authorized"/> the <see cref="AccessToken"/> is the bearer to attach;
/// <see cref="MissingScope"/> names the first scope that was absent (for the user-facing message).
/// </summary>
public sealed record EsiAuthorization(EsiAuthOutcome Outcome, string? AccessToken = null, string? MissingScope = null)
{
    public static EsiAuthorization Authorized(string accessToken) => new(EsiAuthOutcome.Authorized, accessToken);
    public static EsiAuthorization ScopeMissing(string scope) => new(EsiAuthOutcome.ScopeMissing, MissingScope: scope);
    public static readonly EsiAuthorization AuthRequired = new(EsiAuthOutcome.AuthRequired);
}
