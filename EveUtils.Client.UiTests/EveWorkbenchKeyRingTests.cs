using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class EveWorkbenchKeyRingTests
{
    private const long Main = 90000001;
    private const long Alt = 90000002;

    /// <summary>A run nobody chose to upload never leaves; an alt uploads with the main's key.</summary>
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

    /// <summary>Auto-upload on sends a run saved afterwards by itself; off sends nothing.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Save_WithAutoUpload_IsSentOnlyWhenSwitchedOn(bool autoOn)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler();
        using TestClientInstance instance = TestClientInstance.Create(services =>
            services.AddSingleton<IHttpClientFactory>(new HandlerFactory(handler)));
        var publisher = instance.Services.GetRequiredService<EveWorkbenchRunAutoPublisher>();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<EveWorkbenchKeyRing>().AddAsync("main-key", cancellationToken);
        await publisher.SetAutoUploadAsync(autoOn, cancellationToken);

        DateTime started = DateTime.UtcNow.AddMinutes(-20);
        Guid runId = (await dispatcher.Send(new StartRunCommand(Main, ActivityKind.Site, started, 1234, "Blood Refuge", 30000142), cancellationToken)).Value;
        await dispatcher.Send(new SaveRunCommand(runId, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, [], [], [], []), cancellationToken);
        await publisher.WhenIdleAsync();

        Assert.Equal(autoOn, handler.ImportBody?.Contains(runId.ToString()) ?? false);
    }

    /// <summary>A finished upload tells the runs screens, so the chip moves to uploaded without a reselect.</summary>
    [AvaloniaFact]
    public async Task Upload_WhenPublishCompletes_AnnouncesTheRunWithStatusUploaded()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestClientInstance instance = TestClientInstance.Create(services =>
            services.AddSingleton<IHttpClientFactory>(new HandlerFactory(new RecordingHandler())));
        var publisher = instance.Services.GetRequiredService<EveWorkbenchRunAutoPublisher>();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<EveWorkbenchKeyRing>().AddAsync("main-key", cancellationToken);
        DateTime started = DateTime.UtcNow.AddMinutes(-20);
        Guid runId = (await dispatcher.Send(new StartRunCommand(Main, ActivityKind.Site, started, 1234, "Blood Refuge", 30000142), cancellationToken)).Value;
        await dispatcher.Send(new SaveRunCommand(runId, started.AddMinutes(15), started.AddMinutes(16), [], [], [], []), cancellationToken);
        await publisher.WhenIdleAsync();

        string? lastStatus = null;
        using IDisposable subscription = instance.Services.GetRequiredService<RunChangeFeed>().Subscribe(async batch =>
        {
            if (batch.RunIds.Contains(runId))
            {
                lastStatus = await publisher.StatusLabelAsync([runId], cancellationToken);
            }
        });
        await publisher.UploadAsync([runId], cancellationToken);
        await publisher.WhenIdleAsync();
        for (int i = 0; i < 20 && lastStatus != "EWB ✓ uploaded"; i++)
        {
            await Task.Delay(50, cancellationToken);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("EWB ✓ uploaded", lastStatus);
    }

    /// <summary>An upload made while another publish is out on HTTP is still sent: the publish must not overwrite its pending row.</summary>
    [AvaloniaFact]
    public async Task Upload_WhilePublishIsInFlight_IsStillSent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler();
        var savedStates = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using TestClientInstance instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IHttpClientFactory>(new HandlerFactory(handler));
            ServiceDescriptor original = services.Last(descriptor => descriptor.ServiceType == typeof(ISettingRepository));
            services.Remove(original);
            services.Add(new ServiceDescriptor(typeof(ISettingRepository),
                provider => new StateRecordingSettings((ISettingRepository)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), savedStates),
                original.Lifetime));
        });
        var publisher = instance.Services.GetRequiredService<EveWorkbenchRunAutoPublisher>();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<EveWorkbenchKeyRing>().AddAsync("main-key", cancellationToken);
        DateTime started = DateTime.UtcNow.AddMinutes(-20);
        Guid[] runIds = new Guid[2];
        for (int i = 0; i < runIds.Length; i++)
        {
            runIds[i] = (await dispatcher.Send(new StartRunCommand(Main, ActivityKind.Site, started, 1234, "Blood Refuge", 30000142), cancellationToken)).Value;
            await dispatcher.Send(new SaveRunCommand(runIds[i], started.AddMinutes(15), started.AddMinutes(16), [], [], [], []), cancellationToken);
        }

        await publisher.WhenIdleAsync();
        handler.Hold = new TaskCompletionSource();
        await publisher.UploadAsync([runIds[0]], cancellationToken);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await publisher.UploadAsync([runIds[1]], cancellationToken);
        handler.Hold.SetResult();
        await publisher.WhenIdleAsync();

        Assert.Contains(handler.Bodies, body => body.Contains(runIds[1].ToString()));

        // Second step, one run: revision N of run A is out on HTTP while the user changes it again to N+1 and uploads it.
        // The answer for N must not overwrite the pending N+1 — at no moment may the saved revision of A go down.
        handler.Hold = new TaskCompletionSource();
        handler.Entered = new TaskCompletionSource();
        await dispatcher.Send(new DeleteRunCommand(runIds[0], DateTime.UtcNow), cancellationToken);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await dispatcher.Send(new RestoreRunCommand(runIds[0]), cancellationToken);
        await publisher.UploadAsync([runIds[0]], cancellationToken);
        handler.Hold.SetResult();
        await publisher.WhenIdleAsync();

        int[] revisions = [.. savedStates.Select(json =>
            System.Text.Json.JsonDocument.Parse(json).RootElement.TryGetProperty(runIds[0].ToString(), out var entry)
                ? entry.GetProperty("Revision").GetInt32() : 0)];
        Assert.Equal(revisions.Order(), revisions);
    }

    /// <summary>The real settings, plus a record of every value the publisher's state was saved with.</summary>
    private sealed class StateRecordingSettings(ISettingRepository inner, System.Collections.Concurrent.ConcurrentQueue<string> saved) : ISettingRepository
    {
        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);

        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            if (key == EveWorkbenchRunAutoPublisher.StateSettingKey)
            {
                saved.Enqueue(value);
            }

            return inner.UpsertAsync(key, value, cancellationToken);
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
    }

    private sealed class HandlerFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? ImportToken { get; private set; }
        public string? ImportBody { get; private set; }
        public System.Collections.Concurrent.ConcurrentQueue<string> Bodies { get; } = new();
        public TaskCompletionSource? Hold { get; set; }
        public TaskCompletionSource Entered { get; set; } = new();

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
                Bodies.Enqueue(ImportBody);
                Entered.TrySetResult();
                if (Hold is { } hold)
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }

                string[] ids = [.. System.Text.RegularExpressions.Regex.Matches(ImportBody, "\"Id\":\"([0-9a-f-]{36})\"").Select(match => match.Groups[1].Value)];
                body = "{\"Error\":false,\"Results\":[" + string.Join(",", ids.Select(id => $"{{\"ExternalId\":\"{id}\",\"Status\":0}}")) + "]}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
