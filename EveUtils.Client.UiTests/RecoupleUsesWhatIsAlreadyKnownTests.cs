using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Messaging;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Coupling again is the way back from a session the server has dropped, and it was asking for the server address
/// and the label back — both of which are stored with the very coupling being restored. The operator had to look
/// them up and retype them before he could get to the only two steps that actually matter, connecting and signing in
/// (ET-123).
///
/// The address is deliberately not pre-filled everywhere. After a refused certificate the address is precisely what
/// is in question, and handing it back filled in would walk the user past the fingerprint check (ET-95) — which is
/// why the recouple action is offered for <see cref="ServerConnectionState.SessionGone"/> alone, pinned by
/// <c>ServerLinkChipStateTests.EveryOtherState_DoesNotOfferToCoupleAgain</c>.
/// </summary>
public class RecoupleUsesWhatIsAlreadyKnownTests
{
    private const string Server = "https://eve-together.com:7443";
    private const int Abnoba = 90382598;

    [AvaloniaFact]
    public async Task CouplingAgainOpensOnTheKnownAddressAndLabel_SoOnlyTheSignInIsLeft()
    {
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));

        // The coupling that is being restored: a stored session and the label the user chose when they made it.
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("a", "r", "Abnoba Auscent", Abnoba));
        await instance.Services.GetRequiredService<IServerRegistry>()
            .SetAsync(Server, label: "Corp HQ", serverName: "EVE Together");

        var owner = new MainWindowViewModel(instance.Services);
        var dialog = new CharacterDialogViewModel(owner,
            new CharacterViewModel(new Character("Abnoba Auscent", Abnoba)));
        await dialog.InitializeAsync();

        var link = dialog.ServerLinks.Single(l => string.Equals(l.Address, Server, System.StringComparison.OrdinalIgnoreCase));
        link.State = ServerConnectionState.SessionGone;
        Assert.True(link.CanRecouple);

        link.RecoupleCommand.Execute(null);
        await Task.Yield();

        Assert.True(dialogs.CoupleDialogOpened);
        Assert.NotNull(dialogs.LastCouplePrefill);
        Assert.Equal(Server, dialogs.LastCouplePrefill!.Address);
        Assert.Equal("Corp HQ", dialogs.LastCouplePrefill.Label);
    }

    /// <summary>The contrast that keeps the first assertion honest: coupling a server for the first time has nothing
    /// to go on, so that dialog still opens empty rather than on whatever was there last.</summary>
    [AvaloniaFact]
    public async Task CouplingAFreshServerStillOpensWithNothingFilledIn()
    {
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));

        var owner = new MainWindowViewModel(instance.Services);
        await owner.RunCoupleAsync();

        Assert.True(dialogs.CoupleDialogOpened);
        Assert.Null(dialogs.LastCouplePrefill);
    }
}
