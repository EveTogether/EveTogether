namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// The single pivot for all ESI communication. Every typed client and module goes through
/// <see cref="RequestAsync{T}"/>; nobody talks to <c>HttpClient</c> directly. The pivot runs the
/// pre-flight (scope + token) for authed calls, sends through the shared handler chain, and maps the
/// response — success or failure — onto a uniform <see cref="EsiResult{T}"/>.
/// </summary>
public interface IEsiClient
{
    Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sugar for a GET; deserializes the body into <typeparamref name="T"/>. Set <paramref name="expectedNotFound"/>
    /// when a 404 is a routine, caller-handled outcome (e.g. "character is not in a fleet") so the pivot logs it at
    /// Debug instead of Warning.
    /// </summary>
    Task<EsiResult<T>> GetAsync<T>(
        string path,
        int? characterId = null,
        IReadOnlyList<string>? requiredScopes = null,
        CancellationToken cancellationToken = default,
        bool expectedNotFound = false) =>
        RequestAsync<T>(EsiRequest.Get(path, characterId, requiredScopes, expectedNotFound), cancellationToken);
}
