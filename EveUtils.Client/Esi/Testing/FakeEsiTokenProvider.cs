using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi.Testing;

/// <summary>Returns a fixed pre-flight outcome, so the pivot's scope/token gate can be exercised in isolation.</summary>
public sealed class FakeEsiTokenProvider(EsiAuthorization outcome) : IEsiTokenProvider
{
    public Task<EsiAuthorization> AuthorizeAsync(int characterId, IReadOnlyList<string> requiredScopes, CancellationToken cancellationToken = default) =>
        Task.FromResult(outcome);
}
