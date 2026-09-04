using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The commander's "we have started — are you coming?" as it arrives (ET-168, scherm 7). Three answers, and the
/// third one is the absence of a press: <b>later</b> leaves the row standing, and the message keeps standing with
/// it for as long as the fleet runs. The other half of this file is the refusal it exists to replace — accepting an
/// invite while still flying elsewhere is turned down, and the pilot is offered the switch instead of an error.
/// </summary>
public class InboxSwitchRequestTests
{
    private const string Server = "srv:7443";
    private const int Tessa = 400;
    private const int Aurel = 300;
    private const long SwitchMessageId = 5001;
    private const long InviteMessageId = 5002;
    private const long InviteId = 77;
    private const long Sunday = 22;
    private const long Wormhole = 21;

    private static Task SeedAsync(TestClientInstance instance, MessageKind kind, long serverMessageId, long refId, string title) =>
        instance.Services.GetRequiredService<IClientInboxStore>().UpsertAsync(new ClientInboxMessage
        {
            ServerMessageId = serverMessageId,
            ServerAddress = Server,
            RecipientCharacterId = Tessa,
            SenderCharacterId = Aurel,
            Kind = kind,
            RefId = refId,
            Title = title,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ReceivedAt = DateTimeOffset.UnixEpoch,
            Status = MessageStatus.Pending,
        });

    private static async Task<InboxViewModel> InboxAsync(TestClientInstance instance)
    {
        var vm = instance.Services.GetRequiredService<InboxViewModel>();
        for (var i = 0; i < 100 && vm.Messages.Count == 0; i++)
            await Task.Delay(50);
        Assert.NotEmpty(vm.Messages);
        return vm;
    }

    /// <summary>
    /// The row is answerable and its buttons read as what they do. "Switch to this fleet" is a move, not an
    /// acceptance, and "No, I'll stay where I am" is not leaving — so neither may be labelled Accept/Decline. The
    /// third answer gets a line rather than a button, because a row with two buttons reads as one that has to be
    /// settled now.
    /// </summary>
    [AvaloniaFact]
    public async Task ASwitchRequest_IsAnsweredInItsOwnWords_AndLaterIsSaidOutLoud()
    {
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IFleetTransportClient>(transport));
        await SeedAsync(instance, MessageKind.FleetSwitchRequest, SwitchMessageId, Sunday, "We have started — are you coming?");

        var item = Assert.Single((await InboxAsync(instance)).Messages);

        Assert.True(item.IsSwitchRequest);
        Assert.False(item.IsInvite);
        Assert.True(item.CanRespond);
        Assert.Equal("Switch to this fleet", item.AcceptLabel);
        Assert.Equal("No, I'll stay where I am", item.DeclineLabel);
        Assert.True(item.HasLaterHint);
    }

    /// <summary>An ordinary invite keeps the words it always had — the new kind is beside it, not over it.</summary>
    [AvaloniaFact]
    public async Task AnInvite_StillReadsAcceptAndDecline_WithNoTalkOfLater()
    {
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IFleetTransportClient>(transport));
        await SeedAsync(instance, MessageKind.FleetInvite, InviteMessageId, InviteId, "Fleet invite: Wormhole night");

        var item = Assert.Single((await InboxAsync(instance)).Messages);

        Assert.True(item.IsInvite);
        Assert.False(item.IsSwitchRequest);
        Assert.Equal("Accept", item.AcceptLabel);
        Assert.Equal("Decline", item.DeclineLabel);
        Assert.False(item.HasLaterHint);
    }

    /// <summary>Saying no is answered on the server like any other response — and nothing else is called, because
    /// declining a switch touches no roster.</summary>
    [AvaloniaFact]
    public async Task DecliningASwitchRequest_AnswersTheMessageAndNothingElse()
    {
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IFleetTransportClient>(transport));
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Tessa Korrin", Tessa));
        await SeedAsync(instance, MessageKind.FleetSwitchRequest, SwitchMessageId, Sunday, "We have started — are you coming?");

        var vm = await InboxAsync(instance);
        await vm.RespondAsync(Assert.Single(vm.Messages), accept: false);

        var call = Assert.Single(transport.RespondToMessageCalls);
        Assert.Equal(SwitchMessageId, call.MessageId);
        Assert.False(call.Accept);
        Assert.Equal(Tessa, call.ActingCharacterId);
        Assert.Empty(transport.SwitchToFleetCalls);
        Assert.Empty(transport.LeaveCalls);
    }

    /// <summary>
    /// The refusal this ticket is about, turned into a route. Accepting an invite while still flying elsewhere is
    /// turned down by the server; rather than report that and stop, the pilot is shown what a switch would do and,
    /// on their say-so, it is done as one act — which also accepts the invite.
    /// </summary>
    [AvaloniaFact]
    public async Task AnInviteRefusedBecauseYouAreFlyingElsewhere_OffersTheSwitchInstead()
    {
        var transport = new RecordingFleetTransportClient();
        transport.RespondToMessageResult = (false, "Character is already in active fleet 'Wormhole night'. Leave or conclude it before joining another.");
        transport.PendingInvitesByServer[Server] =
            [new FleetInviteInfo(InviteId, Sunday, Aurel, Tessa, FleetRole.SquadMember, FleetInviteStatus.Pending)];
        transport.FleetsById[Sunday] = new FleetInfo(Sunday, "Sunday DED run", null, FleetVisibility.InviteOnly,
            FleetState.Active, Aurel, null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active,
            ActivatedAt: DateTimeOffset.UnixEpoch.AddHours(21));
        transport.MyFleetsByServer[Server] =
        [
            new FleetInfo(Wormhole, "Wormhole night", null, FleetVisibility.Public, FleetState.Active, Aurel, null, null,
                DateTimeOffset.UnixEpoch, FleetActivation.Active, ActivatedAt: DateTimeOffset.UnixEpoch.AddHours(20)),
        ];

        var dialogs = new RecordingDialogService { FleetSwitch = true };
        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(dialogs);
        });
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Tessa Korrin", Tessa));
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Tessa Korrin", Tessa));
        await SeedAsync(instance, MessageKind.FleetInvite, InviteMessageId, InviteId, "Fleet invite: Sunday DED run");

        var vm = await InboxAsync(instance);
        await vm.RespondAsync(Assert.Single(vm.Messages), accept: true);

        // The dialog named both ends, so the pilot saw what they were agreeing to.
        Assert.NotNull(dialogs.FleetSwitchPrompt);
        Assert.Equal("Sunday DED run", dialogs.FleetSwitchPrompt!.TargetFleetName);
        Assert.Equal("Wormhole night", Assert.Single(dialogs.FleetSwitchPrompt.Leaving).FleetName);

        // And it was done as one act, as the character that was asked.
        var call = Assert.Single(transport.SwitchToFleetCalls);
        Assert.Equal(Sunday, call.FleetId);
        Assert.Equal(Tessa, call.ActingCharacterId);
    }

    /// <summary>Backing out of that offer leaves the invite where it was: still pending, still answerable later.</summary>
    [AvaloniaFact]
    public async Task BackingOutOfThatOffer_SwitchesNothing()
    {
        var transport = new RecordingFleetTransportClient();
        transport.RespondToMessageResult = (false, "Character is already in active fleet 'Wormhole night'. Leave or conclude it before joining another.");
        transport.PendingInvitesByServer[Server] =
            [new FleetInviteInfo(InviteId, Sunday, Aurel, Tessa, FleetRole.SquadMember, FleetInviteStatus.Pending)];
        transport.FleetsById[Sunday] = new FleetInfo(Sunday, "Sunday DED run", null, FleetVisibility.InviteOnly,
            FleetState.Active, Aurel, null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active,
            ActivatedAt: DateTimeOffset.UnixEpoch.AddHours(21));
        transport.MyFleetsByServer[Server] =
        [
            new FleetInfo(Wormhole, "Wormhole night", null, FleetVisibility.Public, FleetState.Active, Aurel, null, null,
                DateTimeOffset.UnixEpoch, FleetActivation.Active, ActivatedAt: DateTimeOffset.UnixEpoch.AddHours(20)),
        ];

        var dialogs = new RecordingDialogService { FleetSwitch = false };
        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(dialogs);
        });
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Tessa Korrin", Tessa));
        await SeedAsync(instance, MessageKind.FleetInvite, InviteMessageId, InviteId, "Fleet invite: Sunday DED run");

        var vm = await InboxAsync(instance);
        await vm.RespondAsync(Assert.Single(vm.Messages), accept: true);

        Assert.Empty(transport.SwitchToFleetCalls);
    }
}
