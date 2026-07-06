using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Dtos;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// App-wide toasts raised by the always-alive inbox VM when a fleet action is delivered: a fleet-start with an
/// "Open metrics" button, and an invite with Accept/Decline that reuse the inbox response handler. Drives the real
/// InboxViewModel over the throwaway client DI + a live MessageDeliveredEvent on the in-process bus. A first-time
/// delivery toasts once; a reconnect replay and multibox copies do not re-toast.
/// </summary>
public class InboxToastTests
{
    private const string ServerA = "srvA:7443";
    private const long FleetId = 555;

    [AvaloniaFact]
    public async Task FleetStartedDelivery_RaisesOpenMetricsToast_ThatLaunchesTheFleet()
    {
        var toasts = new RecordingToastService();
        var launcher = new RecordingFleetMetricsLauncher();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IFleetMetricsLauncher>(launcher);
        });
        _ = ResolveInbox(instance);

        await DeliverAsync(instance, FleetStarted(serverMessageId: 1, recipient: 100));
        await WaitForToastAsync(toasts);

        var toast = Assert.Single(toasts.ActionToasts);
        Assert.Equal("Fleet started: Roam", toast.Title);
        Assert.Equal(ToastKind.Success, toast.Kind);
        var action = Assert.Single(toast.Actions);
        Assert.Equal("Open metrics", action.Label);

        action.Run();

        var launch = Assert.Single(launcher.LaunchCalls);
        Assert.Equal((ServerA, FleetId, 100), launch);    }

    [AvaloniaFact]
    public async Task InviteDelivery_RaisesAcceptDeclineToast_ThatRespondsViaTheServer()
    {
        var toasts = new RecordingToastService();
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IFleetTransportClient>(transport);
        });
        await RegisterCharacterAsync(instance, "Alpha", 100);
        _ = ResolveInbox(instance);

        await DeliverAsync(instance, Invite(serverMessageId: 42, recipient: 100));
        await WaitForToastAsync(toasts);

        var toast = Assert.Single(toasts.ActionToasts);
        Assert.Equal("Fleet invite: Roam", toast.Title);
        Assert.Equal(ToastKind.Information, toast.Kind);
        Assert.Equal(new[] { "Accept", "Decline" }, toast.Actions.Select(a => a.Label).ToArray());
        Assert.Equal(ToastActionStyle.Affirmative, toast.Actions[0].Style); // green
        Assert.Equal(ToastActionStyle.Destructive, toast.Actions[1].Style); // red

        toast.Actions.First(a => a.Label == "Accept").Run();
        for (var i = 0; i < 100 && transport.RespondToMessageCalls.Count == 0; i++)
            await Task.Delay(50);

        var call = Assert.Single(transport.RespondToMessageCalls);
        Assert.Equal(42, call.MessageId);
        Assert.Equal(100, call.ActingCharacterId);
        Assert.True(call.Accept);    }

    [AvaloniaFact]
    public async Task ReplayedDelivery_DoesNotRaiseASecondToast()
    {
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IFleetMetricsLauncher>(new RecordingFleetMetricsLauncher());
        });
        _ = ResolveInbox(instance);

        // The same delivery event re-fires on reconnect (replay) — identical server-message id + recipient.
        await DeliverAsync(instance, FleetStarted(serverMessageId: 1, recipient: 100));
        await WaitForToastAsync(toasts);
        await DeliverAsync(instance, FleetStarted(serverMessageId: 1, recipient: 100));
        await Task.Delay(150);

        Assert.Single(toasts.ActionToasts);    }

    [AvaloniaFact]
    public async Task MultiboxDelivery_ToTwoCharacters_RaisesOneToast()
    {
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IFleetMetricsLauncher>(new RecordingFleetMetricsLauncher());
        });
        _ = ResolveInbox(instance);

        // One fleet start delivered to two of my characters: distinct server-message ids, same action.
        await DeliverAsync(instance, FleetStarted(serverMessageId: 1, recipient: 100));
        await WaitForToastAsync(toasts);
        await DeliverAsync(instance, FleetStarted(serverMessageId: 2, recipient: 200));
        await Task.Delay(150);

        Assert.Single(toasts.ActionToasts);    }

    [AvaloniaFact]
    public async Task ReplayedOldDelivery_OnReconnect_DoesNotToast()
    {
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IFleetMetricsLauncher>(new RecordingFleetMetricsLauncher());
        });
        _ = ResolveInbox(instance);

        // A reconnect replays the still-pending invites; their CreatedAt is from before this session started.
        await DeliverAsync(instance, Invite(serverMessageId: 70, recipient: 100, createdAt: DateTimeOffset.UnixEpoch));
        await Task.Delay(200);

        Assert.Empty(toasts.ActionToasts);
    }

    [AvaloniaFact]
    public async Task InviteToastAccept_MarksTheInboxMessageResponded()
    {
        var toasts = new RecordingToastService();
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IFleetTransportClient>(transport);
        });
        await RegisterCharacterAsync(instance, "Alpha", 100);
        var inbox = ResolveInbox(instance);

        await DeliverAsync(instance, Invite(serverMessageId: 42, recipient: 100));
        await WaitForToastAsync(toasts);

        toasts.ActionToasts[0].Actions.First(a => a.Label == "Accept").Run();
        for (var i = 0; i < 100 && (transport.RespondToMessageCalls.Count == 0 || inbox.Messages.Any(m => m.CanRespond)); i++)
            await Task.Delay(50);

        var row = Assert.Single(inbox.Messages);
        Assert.False(row.CanRespond);                       // the invite is no longer actionable in the inbox
        Assert.Equal(MessageStatus.Responded, row.Status);  // the inbox message is updated with the choice
    }

    private static MessageDeliveredPayload FleetStarted(long serverMessageId, int recipient, DateTimeOffset? createdAt = null) => new(
        serverMessageId, recipient, 4101, MessageKind.FleetStarted, FleetId,
        "Fleet started: Roam", "Roam has started — open its metrics to see the fleet live.",
        null, createdAt ?? DateTimeOffset.UtcNow);

    private static MessageDeliveredPayload Invite(long serverMessageId, int recipient, DateTimeOffset? createdAt = null) => new(
        serverMessageId, recipient, 4101, MessageKind.FleetInvite, 9001,
        "Fleet invite: Roam", "You are invited.",
        null, createdAt ?? DateTimeOffset.UtcNow);

    private static InboxViewModel ResolveInbox(TestClientInstance instance) =>
        instance.Services.GetRequiredService<InboxViewModel>(); // subscribes to MessageDeliveredEvent in its ctor

    private static Task DeliverAsync(TestClientInstance instance, MessageDeliveredPayload payload) =>
        instance.Services.GetRequiredService<IEventBus>()
            .PublishAsync(new MessageDeliveredEvent(payload) { SourceServerAddress = ServerA });

    private static async Task WaitForToastAsync(RecordingToastService toasts)
    {
        for (var i = 0; i < 100 && toasts.ActionToasts.Count == 0; i++)
            await Task.Delay(50);
        Assert.NotEmpty(toasts.ActionToasts);
    }

    private static Task RegisterCharacterAsync(TestClientInstance instance, string name, int id) =>
        instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, id));
}
