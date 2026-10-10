using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class EveWorkbenchRunPublisherTests
{
    [Fact]
    public async Task PublishAsync_Disabled_DoesNotSend()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var publisher = new EveWorkbenchRunPublisher(new HttpClient(handler));
        await publisher.PublishAsync(false, "https://workbench.example/", "token", [_Payload()], cancellationToken);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task PublishAsync_EnabledSavedRun_SendsUnchangedPayload()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var handler = new Handler(_ => _Response("{\"Error\":false,\"Results\":[]}"));
        RunWirePayload payload = _Payload();
        var publisher = new EveWorkbenchRunPublisher(new HttpClient(handler));
        await publisher.PublishAsync(true, "https://workbench.example/", "token", [payload], cancellationToken);
        Assert.Equal("/v1/runs/import", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("token", handler.Request.Headers.GetValues("Character-Access-Token").Single());
        Assert.Equal(JsonSerializer.Serialize(new { Runs = new[] { payload } }, new JsonSerializerOptions()), handler.Body);
    }

    [Fact]
    public async Task PublishAsync_NetworkFailure_KeepsPayloadPending()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RunWirePayload payload = _Payload();
        var publisher = new EveWorkbenchRunPublisher(new HttpClient(new Handler(_ => throw new HttpRequestException())));
        EveWorkbenchRunPublishOutcome outcome = await publisher.PublishAsync(true, "https://workbench.example/", "token", [payload], cancellationToken);
        Assert.Equal([payload], outcome.Pending);
    }

    [Fact]
    public async Task PublishAsync_RejectedResponse_LogsAndCompletes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RunWirePayload payload = _Payload();
        var logger = new Logger();
        var publisher = new EveWorkbenchRunPublisher(new HttpClient(new Handler(_ => _Response($"{{\"Error\":false,\"Results\":[{{\"ExternalId\":\"{payload.Run.Id}\",\"Status\":3,\"Reason\":\"invalid\"}}]}}"))), logger);
        EveWorkbenchRunPublishOutcome outcome = await publisher.PublishAsync(true, "https://workbench.example/", "token", [payload], cancellationToken);
        Assert.Empty(outcome.Pending);
        Assert.Contains("Rejected", logger.Message);
    }

    [Fact]
    public async Task PublishAsync_RefusedKey_ReportsUnauthorizedAndKeepsPayloadPending()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RunWirePayload payload = _Payload();
        var publisher = new EveWorkbenchRunPublisher(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        EveWorkbenchRunPublishOutcome outcome = await publisher.PublishAsync(true, "https://workbench.example/", "token", [payload], cancellationToken);
        Assert.True(outcome.Unauthorized);
        Assert.Equal([payload], outcome.Pending);
    }

    private static RunWirePayload _Payload() => new() { SentAtUnixMilliseconds = 123, Run = RunWireData.FromEntity(new Run { Id = Guid.Parse("11111111-1111-1111-1111-111111111111") }) };
    private static HttpResponseMessage _Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler { public HttpRequestMessage? Request { get; private set; } public string? Body { get; private set; } protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Request = request; Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken); return response(request); } }
    private sealed class Logger : IEveWorkbenchRunPublishLogger { public string Message { get; private set; } = string.Empty; public void Log(string message) { Message = message; } }
}
