using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Messaging;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Where ET-123 stopped short in production. The client detected a swept session correctly and said so in its log —
/// <c>"no longer has the session for character 90382598; the heartbeat stopped"</c> — and the operator's screen showed
/// nothing at all. Correct data behind a display that does not show it.
///
/// The cause was that connection state travelled per SERVER only. Six characters share one server, and the roll-up is
/// deliberately "the best of them", so five healthy characters answered <c>Connected</c> for the sixth
/// (measured: <c>5xConnected + 1xSessionGone -> Connected</c>). The receiving end then painted that one state onto
/// every link of that address, so a per-character state could not be shown in either direction — it was either hidden
/// by its neighbours or smeared across them.
///
/// <see cref="ServerLinkChipStateTests"/> pins what a chip shows for a given state and passed throughout, because it
/// asks one link in isolation. This asks the question that failed: does the state reach the right chip, and only it.
/// </summary>
public class ServerLinkStateReachesTheRightChipTests
{
    private const string Server = "https://eve-together.com:7443";
    private const int Abnoba = 90382598;
    private static readonly int[] Healthy = [90250177, 2121667919, 2121207351, 2123169375, 2122696898];

    [AvaloniaFact]
    public async Task ASweptSessionShowsOnItsOwnCharactersChip_WhileTheOthersOnThatServerStayConnected()
    {
        var connector = new FakeRemoteBusConnector();
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IRemoteBusConnector>(connector));

        var owner = new MainWindowViewModel(instance.Services);
        foreach (var characterId in Healthy.Append(Abnoba))
            owner.Characters.Add(Card(characterId));

        // Everyone connects, as they did before the session was deleted on the server.
        foreach (var characterId in Healthy.Append(Abnoba))
            connector.RaiseCharacterStateChanged(Server, characterId, ServerConnectionState.Connected);
        Dispatcher.UIThread.RunJobs();

        // The server drops one character's session. This is the moment the log line was written and the screen was not.
        connector.RaiseCharacterStateChanged(Server, Abnoba, ServerConnectionState.SessionGone);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ServerConnectionState.SessionGone, Link(owner, Abnoba).State);
        Assert.All(Healthy, id => Assert.Equal(ServerConnectionState.Connected, Link(owner, id).State));

        // And the banner names the server, so the one character in trouble is not lost among five healthy ones.
        Assert.True(owner.IsServerPairingAlert);
        Assert.Contains("couple the character again", owner.ServerPairingAlertMessage);
    }

    private static CharacterViewModel Card(int characterId)
    {
        var card = new CharacterViewModel(new Character($"Char{characterId}", characterId));
        card.ServerLinks.Add(new ServerLinkViewModel(
            characterId, Server, "EVE Together", ServerConnectionState.Connecting, _ => Task.CompletedTask));
        return card;
    }

    private static ServerLinkViewModel Link(MainWindowViewModel owner, int characterId) =>
        owner.Characters.Single(c => c.CharacterId == characterId).ServerLinks.Single();
}
