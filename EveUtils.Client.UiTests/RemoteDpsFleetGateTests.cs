using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Events;
using EveUtils.Shared.Modules.Gamelog.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-9: DPS reaches <see cref="EventTarget.Remote"/> only for an active participant of a server fleet. The
/// publishing case is the counter-proof, since never publishing at all would pass the other one for free.</summary>
public class RemoteDpsFleetGateTests
{
    private const int CharacterId = 95000123;
    private const long FleetId = 4242;

    // No fleet at all, and a client-only fleet, whose samples never leave this machine.
    public static TheoryData<FleetParticipant[]> NoActiveServerFleet => new()
    {
        { [] },
        { [new FleetParticipant(CharacterId, FleetId, ClientOnly: true)] }
    };

    [AvaloniaTheory]
    [MemberData(nameof(NoActiveServerFleet))]
    public async Task NoActiveServerFleet_NeverPublishesRemote(FleetParticipant[] participants)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new RecordingTransport();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(transport));

        instance.Services.GetRequiredService<IFleetParticipation>().Set(participants);

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(CharacterId, "Pilot");
        await gamelog.AddHitAsync("Pilot", DamageDirection.Outgoing, 500, "Rat");

        await gamelog.PublishRemoteTickAsync(cancellationToken);

        Assert.Empty(transport.Sent);
    }

    [AvaloniaFact]
    public async Task ActiveServerFleetMember_PublishesRemote()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new RecordingTransport();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(transport));

        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(CharacterId, FleetId, ClientOnly: false)]);

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(CharacterId, "Pilot");
        await gamelog.AddHitAsync("Pilot", DamageDirection.Outgoing, 500, "Rat");

        await gamelog.PublishRemoteTickAsync(cancellationToken);

        // NotEmpty rather than Single: the constructor's own 150 ms background sampler (RemotePublishLoopAsync)
        // keeps running alongside this deterministic call and may add its own gated tick in the same window —
        // always for this same character, since nothing else is racing it.
        Assert.NotEmpty(transport.Sent);
        Assert.All(transport.Sent, sent => Assert.Equal(CharacterId, sent.CharacterId));
    }

    private sealed class RecordingTransport : IRemoteEventTransport
    {
        public List<CombatLoggedEvent> Sent { get; } = [];

        public Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            if (integrationEvent is CombatLoggedEvent combat)
            {
                Sent.Add(combat);
            }

            return Task.CompletedTask;
        }
    }
}
