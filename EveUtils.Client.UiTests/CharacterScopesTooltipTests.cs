using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Location;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The character dialog says what a character actually shares, read from the stored grant — never from the scopes
/// this build would like to have. Where those two differ is the interesting part.
/// </summary>
public class CharacterScopesTooltipTests
{
    [AvaloniaFact]
    public async Task TheTooltip_NamesTheGrantedScopes_AndNotTheOnesTheAppMerelyWants()
    {
        using var instance = TestClientInstance.Create();
        var owner = new MainWindowViewModel(instance.Services);
        var character = new CharacterViewModel(new Character("RaymondKrah", 90250177,
            [LocationScopeCatalog.ReadLocation, "esi-characters.read_notifications.v1"]));

        var dialog = new CharacterDialogViewModel(owner, character);
        await dialog.InitializeAsync();

        // Granted and declared: named after the feature that asked for it.
        Assert.Contains("Location", dialog.ScopesTooltip);
        Assert.Contains(LocationScopeCatalog.ReadLocation, dialog.ScopesTooltip);

        // Granted but not declared by this build: the raw scope, rather than being dropped or half-translated.
        Assert.Contains("esi-characters.read_notifications.v1", dialog.ScopesTooltip);

        // Declared by this build but never granted: must not appear, or the tooltip would describe the wish list.
        Assert.DoesNotContain(FittingsScopeCatalog.ReadFittings, dialog.ScopesTooltip);
    }

    [AvaloniaFact]
    public async Task ACharacterWithoutScopes_SaysSoRatherThanShowingNothing()
    {
        using var instance = TestClientInstance.Create();
        var owner = new MainWindowViewModel(instance.Services);
        var dialog = new CharacterDialogViewModel(owner,
            new CharacterViewModel(new Character("Catbank", 90250178)));

        await dialog.InitializeAsync();

        Assert.Contains("No ESI scopes granted", dialog.ScopesTooltip);
    }
}
