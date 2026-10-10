using System.Net.Http.Json;
using System.Text.Json;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.Runs;

public interface IEveWorkbenchRunPublishLogger
{
    void Log(string message);
}

/// <summary>The HTTP leg of the EVE Workbench publish (ET-325): ET's own <see cref="RunWirePayload"/>, unchanged, in one
/// POST v1/runs/import. Knows nothing of settings or storage; the caller keeps what comes back as pending.</summary>
public sealed class EveWorkbenchRunPublisher(HttpClient client, IEveWorkbenchRunPublishLogger? logger = null)
{
    public const string HttpClientName = "EveWorkbenchRunPublisher";

    /// <summary>EVE Workbench refuses a request with more runs than this.</summary>
    public const int MaxRunsPerRequest = 50;

    // PascalCase on purpose: the import contract names its fields that way, and the payload must not be reshaped.
    private static readonly JsonSerializerOptions RequestOptions = new();
    private static readonly JsonSerializerOptions ResponseOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Disabled sends nothing and leaves nothing pending. Otherwise <see cref="EveWorkbenchRunPublishOutcome.Pending"/>
    /// holds what EVE Workbench did not answer for, which the caller sends again later.</summary>
    public async Task<EveWorkbenchRunPublishOutcome> PublishAsync(bool enabled, string baseUrl, string? token,
        IReadOnlyList<RunWirePayload> payloads, CancellationToken cancellationToken = default)
    {
        if (!enabled || payloads.Count == 0)
        {
            return new EveWorkbenchRunPublishOutcome([], []);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new EveWorkbenchRunPublishOutcome(payloads, []);
        }

        List<RunWirePayload> pending = [];
        bool unauthorized = false;
        List<EveWorkbenchRunImportResult> results = [];
        foreach (RunWirePayload[] batch in payloads.Chunk(MaxRunsPerRequest))
        {
            if (unauthorized)
            {
                pending.AddRange(batch);
                continue;
            }

            (IReadOnlyList<EveWorkbenchRunImportResult>? answered, unauthorized) = await _SendAsync(baseUrl, token, batch, cancellationToken);
            if (answered is null)
            {
                pending.AddRange(batch);
                continue;
            }

            results.AddRange(answered);
            pending.AddRange(batch.Where(payload => answered.All(result => result.ExternalId != payload.Run.Id)));
        }

        return new EveWorkbenchRunPublishOutcome(pending, results, unauthorized);
    }

    /// <summary>Asks EVE Workbench who the key belongs to: GET v1/characters answers the account's main character and
    /// its toons, which v1/me (one name) cannot. A 401/403 is a key EVE Workbench does not accept.</summary>
    public async Task<EveWorkbenchKeyCheck> CheckKeyAsync(string baseUrl, string token, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), "v1/characters"));
            request.Headers.Add("Character-Access-Token", token);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new EveWorkbenchKeyCheck(EveWorkbenchKeyVerdict.Invalid, []);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new EveWorkbenchKeyCheck(EveWorkbenchKeyVerdict.Unreachable, []);
            }

            EveWorkbenchCharacter[] characters = await response.Content.ReadFromJsonAsync<EveWorkbenchCharacter[]>(ResponseOptions, cancellationToken) ?? [];
            return new EveWorkbenchKeyCheck(EveWorkbenchKeyVerdict.Valid, characters);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return new EveWorkbenchKeyCheck(EveWorkbenchKeyVerdict.Unreachable, []);
        }
    }

    /// <summary>Null results when the batch got no usable answer at all, so every run in it stays pending.</summary>
    private async Task<(IReadOnlyList<EveWorkbenchRunImportResult>? Results, bool Unauthorized)> _SendAsync(string baseUrl, string token,
        RunWirePayload[] batch, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "v1/runs/import"))
            {
                Content = JsonContent.Create(new EveWorkbenchRunImportRequest { Runs = batch }, options: RequestOptions)
            };
            request.Headers.Add("Character-Access-Token", token);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger?.Log($"EVE Workbench run import failed with HTTP {(int)response.StatusCode}.");
                return (null, response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);
            }

            EveWorkbenchRunImportResponse? body = await response.Content.ReadFromJsonAsync<EveWorkbenchRunImportResponse>(ResponseOptions, cancellationToken);
            if (body is null || body.Error)
            {
                logger?.Log($"EVE Workbench run import was refused: {body?.Message}");
                return (null, false);
            }

            foreach (EveWorkbenchRunImportResult result in body.Results)
            {
                logger?.Log($"EVE Workbench run {result.ExternalId} import result: {result.Status}{(result.Reason is null ? string.Empty : $" ({result.Reason})")}.");
            }

            return (body.Results, false);
        }
        catch (JsonException)
        {
            logger?.Log("EVE Workbench run import returned an answer that could not be read.");
            return (null, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger?.Log("EVE Workbench run import could not reach the configured endpoint.");
            return (null, false);
        }
    }
}

public sealed class EveWorkbenchRunImportRequest
{
    public required IReadOnlyList<RunWirePayload> Runs { get; init; }
}

public sealed class EveWorkbenchRunImportResponse
{
    public bool Error { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<EveWorkbenchRunImportResult> Results { get; init; } = [];
}

/// <summary>Numeric on the wire, in this order.</summary>
public enum EveWorkbenchRunImportStatus
{
    Created,
    Updated,
    Ignored,
    Rejected
}

public sealed class EveWorkbenchRunImportResult
{
    public required Guid ExternalId { get; init; }
    public required EveWorkbenchRunImportStatus Status { get; init; }
    public string? Reason { get; init; }
    public Guid? RunId { get; init; }
    public Guid? RunGroupId { get; init; }
}

public sealed record EveWorkbenchRunPublishOutcome(IReadOnlyList<RunWirePayload> Pending,
    IReadOnlyList<EveWorkbenchRunImportResult> Results, bool Unauthorized = false);

public enum EveWorkbenchKeyVerdict
{
    Valid,
    Invalid,
    Unreachable
}

public sealed class EveWorkbenchCharacter
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed record EveWorkbenchKeyCheck(EveWorkbenchKeyVerdict Verdict, IReadOnlyList<EveWorkbenchCharacter> Characters);
