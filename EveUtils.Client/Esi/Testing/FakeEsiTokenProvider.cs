using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi.Testing;

/// <summary>Returns a fixed pre-flight outcome, so the pivot's scope/token gate can be exercised in isolation. Given
/// <paramref name="grantedScopes"/>, a request for any scope outside them is ScopeMissing, like the real provider.</summary>
public sealed class FakeEsiTokenProvider(EsiAuthorization outcome, IReadOnlyCollection<string>? grantedScopes = null)
    : IEsiTokenProvider
{
    public Task<EsiAuthorization> AuthorizeAsync(int characterId, IReadOnlyList<string> requiredScopes, CancellationToken cancellationToken = default) =>
        Task.FromResult(grantedScopes is not null && requiredScopes.FirstOrDefault(scope => !grantedScopes.Contains(scope)) is { } missing
            ? EsiAuthorization.ScopeMissing(missing)
            : outcome);
}
