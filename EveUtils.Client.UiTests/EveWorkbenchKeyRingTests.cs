using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class EveWorkbenchKeyRingTests
{
    private const long Main = 90000001;
    private const long Alt = 90000002;

    /// <summary>A run nobody chose to upload never leaves. A key is stored once for its EVE Workbench account: an alt on it uploads with the main's key
    /// without entering anything, and its saved run goes out with that key.</summary>
    [AvaloniaFact]
    public async Task Save_RunIsOnlyUploadedWhenChosen_AndAnAltUsesTheMainsKey()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler();
        using TestClientInstance instance = TestClientInstance.Create(services =>
            services.AddSingleton<IHttpClientFactory>(new HandlerFactory(handler)));
        var ring = instance.Services.GetRequiredService<EveWorkbenchKeyRing>();
        var publisher = instance.Services.GetRequiredService<EveWorkbenchRunAutoPublisher>();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        await ring.AddAsync("main-key", cancellationToken);
        Assert.False((await ring.ForPilotAsync(Alt, cancellationToken))!.IsOwn);
        var started = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        Guid runId = (await dispatcher.Send(new StartRunCommand(Alt, ActivityKind.Site, started, 1234, "Blood Refuge", 30000142), cancellationToken)).Value;
        await dispatcher.Send(new SaveRunCommand(runId, started.AddMinutes(15), started.AddMinutes(16), [], [], [], []), cancellationToken);
        await publisher.WhenIdleAsync();
        Assert.Null(handler.ImportBody);

        await publisher.UploadAsync([runId], cancellationToken);
        await publisher.WhenIdleAsync();

        Assert.Equal("main-key", handler.ImportToken);
        Assert.Contains(runId.ToString(), handler.ImportBody);
    }

    private sealed class HandlerFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? ImportToken { get; private set; }
        public string? ImportBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.Method == HttpMethod.Get)
            {
                body = $"[{{\"Id\":{Main},\"Name\":\"Main\"}},{{\"Id\":{Alt},\"Name\":\"Alt\"}}]";
            }
            else
            {
                ImportToken = request.Headers.GetValues("Character-Access-Token").Single();
                ImportBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                string[] ids = [.. System.Text.RegularExpressions.Regex.Matches(ImportBody, "\"Id\":\"([0-9a-f-]{36})\"").Select(match => match.Groups[1].Value)];
                body = "{\"Error\":false,\"Results\":[" + string.Join(",", ids.Select(id => $"{{\"ExternalId\":\"{id}\",\"Status\":0}}")) + "]}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
