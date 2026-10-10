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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "v1/runs/import"))
            {
                Content = JsonContent.Create(new EveWorkbenchRunImportRequest { Runs = payloads }, options: RequestOptions)
            };
            request.Headers.Add("Character-Access-Token", token);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger?.Log($"EVE Workbench run import failed with HTTP {(int)response.StatusCode}.");
                return new EveWorkbenchRunPublishOutcome(payloads, []);
            }

            IReadOnlyList<EveWorkbenchRunImportResult> results =
                await response.Content.ReadFromJsonAsync<IReadOnlyList<EveWorkbenchRunImportResult>>(ResponseOptions, cancellationToken) ?? [];
            foreach (EveWorkbenchRunImportResult result in results)
            {
                logger?.Log($"EVE Workbench run {result.ExternalId} import result: {result.Status}{(result.Reason is null ? string.Empty : $" ({result.Reason})")}.");
            }

            Guid[] answered = [.. results.Where(result => result.Status is "Created" or "Updated" or "Ignored" or "Rejected").Select(result => result.ExternalId)];
            return new EveWorkbenchRunPublishOutcome(payloads.Where(payload => !answered.Contains(payload.Run.Id)).ToArray(), results);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger?.Log("EVE Workbench run import could not reach the configured endpoint.");
            return new EveWorkbenchRunPublishOutcome(payloads, []);
        }
    }
}

public sealed class EveWorkbenchRunImportRequest
{
    public required IReadOnlyList<RunWirePayload> Runs { get; init; }
}

public sealed class EveWorkbenchRunImportResult
{
    public required Guid ExternalId { get; init; }
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public Guid? RunId { get; init; }
    public Guid? RunGroupId { get; init; }
}

public sealed record EveWorkbenchRunPublishOutcome(IReadOnlyList<RunWirePayload> Pending,
    IReadOnlyList<EveWorkbenchRunImportResult> Results);
