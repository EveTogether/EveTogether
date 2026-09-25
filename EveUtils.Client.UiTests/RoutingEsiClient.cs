using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.UiTests;

/// <summary>An <see cref="IEsiClient"/> that answers each typed GET from a per-path response table. Promoted out of
/// <c>EsiSkillImporterPersistenceTests</c> (ET-387) so other importer tests can reuse it instead of a second fake.</summary>
internal sealed class RoutingEsiClient : IEsiClient
{
    public Dictionary<string, object?> Responses { get; } = new();

    public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Responses.TryGetValue(request.Path, out var value) && value is T typed
            ? EsiResult<T>.Ok(typed)
            : EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ServerError, $"no stub for {request.Path}", 500)));
}
