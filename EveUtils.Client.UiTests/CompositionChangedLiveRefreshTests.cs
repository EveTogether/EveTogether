using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A composition changing must reach every open compositions window, not only the one that made the change: a
/// client-only composition changed anywhere on this client, a shared one changed by another client (pushed by the
/// server), and an editor that holds unsaved edits and must not lose them to either.
/// </summary>
public class CompositionChangedLiveRefreshTests
{
    private const int Owner = 95400001;
    private const int OtherOwner = 95400002;
    private const string Server = "https://eve-together.com";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }

    private static LocalFleetCompositionClient LocalClient(IServiceProvider services, int owner) =>
        new(services.GetRequiredService<ClientFleetService>(),
            services.GetRequiredService<IFleetCompositionRepository>(), owner);

    [AvaloniaFact]
    public async Task AClientOnlyComposition_ChangedOutsideTheWindow_AppearsInTheOpenList()
    {
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(new RecordingDialogService()));
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Pilot One", Owner));
        using var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        Assert.Empty(vm.SelectedTab!.Compositions);

        await LocalClient(instance.Services, Owner).CreateAsync("Shield doctrine", null);

        await WaitUntilAsync(() => vm.SelectedTab!.Compositions.Count == 1);
        Assert.Equal("Shield doctrine", Assert.Single(vm.SelectedTab!.Compositions).Name);
    }

    [AvaloniaFact]
    public async Task ASharedComposition_ChangedByAnotherClient_AppearsInTheOpenServerList()
    {
        var transport = new RecordingFleetTransportClient();
        var connector = new FakeRemoteBusConnector();
        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IRemoteBusConnector>(connector);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Pilot One", Owner));
        connector.RaiseStateChanged(Server, ServerConnectionState.Connected);

        using var vm = new CompositionsViewModel(instance.Services);
        await WaitUntilAsync(() => vm.Tabs.Any(t => !t.IsLocal));
        var serverTab = vm.Tabs.Single(t => !t.IsLocal);
        await serverTab.ReloadAsync();
        Assert.Empty(serverTab.Loaded);

        transport.CompositionsByServer[Server] =
            [new FleetCompositionInfo(7, "Armor HAW", null, OtherOwner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)];
        var pushed = instance.Services.GetRequiredService<IEventTypeRegistry>().Deserialize(
            "composition.changed",
            JsonSerializer.Serialize(new CompositionChangePayload(7, CompositionChangeKind.Created, IsClientOnly: false)),
            OtherOwner);
        ((IServerSourcedEvent)pushed!).SourceServerAddress = Server;
        await instance.Services.GetRequiredService<IEventBus>().PublishAsync(pushed);

        await WaitUntilAsync(() => serverTab.Loaded.Count == 1);
        Assert.Equal("Armor HAW", Assert.Single(serverTab.Loaded).Name);
    }

    [AvaloniaFact]
    public async Task AnEditorWithUnsavedEdits_IsNotOverwritten_ByAChangeMadeElsewhere()
    {
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(new RecordingDialogService()));
        var client = LocalClient(instance.Services, Owner);
        var compositionId = (await client.CreateAsync("Armor doctrine", null)).Id;
        using var editor = CompositionEditorViewModel.ForExisting(instance.Services, client, (await client.GetAsync(compositionId))!);
        editor.Name = "My unsaved rename";

        await client.EditAsync(compositionId, "Renamed by someone else", null);
        await WaitUntilAsync(() => editor.HasRemoteChange);

        Assert.True(editor.HasRemoteChange);
        Assert.Equal("My unsaved rename", editor.Name);
        Assert.Contains("Changed elsewhere", editor.Status);

        await editor.ReloadCommand.ExecuteAsync(null);

        Assert.Equal("Renamed by someone else", editor.Name);
        Assert.False(editor.HasRemoteChange);
    }
}
