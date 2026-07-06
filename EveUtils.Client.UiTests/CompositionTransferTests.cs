using System.Linq;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Composition push/download: downloading a server composition recreates
/// its whole graph in the local library owned by the chosen character, and a name already owned by the target is
/// skipped (compositions carry no content hash, so the name is the dedup). Push to a real server needs a running
/// server, so the copy logic is exercised through the download direction with a local-backed source row.
/// </summary>
public class CompositionTransferTests
{
    private const int OwnerA = 95400001;
    private const int OwnerB = 95400002;

    private static async Task SeedCharacterAsync(IServiceProvider services, int characterId, string name) =>
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));

    private static LocalFleetCompositionClient LocalClient(IServiceProvider services, int owner) =>
        new(services.GetRequiredService<ClientFleetService>(),
            services.GetRequiredService<IFleetCompositionRepository>(), owner);

    private static FitReferenceInfo Fit(string name, int shipTypeId) =>
        new(shipTypeId, name, "{}", name + shipTypeId, null, null);

    private static async Task<long> BuildSourceAsync(LocalFleetCompositionClient client)
    {
        var (_, _, compositionId) = await client.CreateAsync("Armor doctrine", "armor + shield");
        var role = await client.AddRoleAsync(compositionId, "Logistics", 5);
        await client.AddEntryAsync(role.Id, Fit("Guardian", 11987), 3);
        await client.AddEntryAsync(role.Id, Fit("Scimitar", 11978), 2);
        return compositionId;
    }

    [AvaloniaFact]
    public async Task Download_CopiesGraph_ToLocalLibrary_ForChosenOwner()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, OwnerA, "Pilot A");
        await SeedCharacterAsync(instance.Services, OwnerB, "Pilot B");

        var source = LocalClient(instance.Services, OwnerA);
        await BuildSourceAsync(source);
        var info = (await source.ListAsync()).Single();

        recording.OnPickCharacter = (_, _) => Task.FromResult<int?>(OwnerB);   // download as Pilot B

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        var row = new CompositionRowViewModel(info, "Pilot A", canEdit: true, isLocal: false, source);

        await vm.DownloadCompositionCommand.ExecuteAsync(row);

        var targetClient = LocalClient(instance.Services, OwnerB);
        var copiedInfo = Assert.Single(await targetClient.ListAsync());
        var copied = await targetClient.GetAsync(copiedInfo.Id);
        Assert.Equal("Armor doctrine", copied!.Composition.Name);
        var copiedRole = Assert.Single(copied.Roles);
        Assert.Equal("Logistics", copiedRole.RoleName);
        Assert.Equal(5, copiedRole.GroupMinCount);
        Assert.Equal(2, copiedRole.Entries.Count);
        Assert.Contains(copiedRole.Entries, e => e.Fit.FitName == "Guardian" && e.EntryMinCount == 3);
    }

    [AvaloniaFact]
    public async Task Download_SameNameAlreadyOwned_IsSkipped()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, OwnerA, "Pilot A");
        await SeedCharacterAsync(instance.Services, OwnerB, "Pilot B");

        var source = LocalClient(instance.Services, OwnerA);
        await BuildSourceAsync(source);
        var info = (await source.ListAsync()).Single();

        // Pilot B already owns a composition with the same name → the download must not duplicate it.
        await LocalClient(instance.Services, OwnerB).CreateAsync("Armor doctrine", null);

        recording.OnPickCharacter = (_, _) => Task.FromResult<int?>(OwnerB);

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        var row = new CompositionRowViewModel(info, "Pilot A", canEdit: true, isLocal: false, source);

        await vm.DownloadCompositionCommand.ExecuteAsync(row);

        Assert.Single(await LocalClient(instance.Services, OwnerB).ListAsync());   // still just the one
        Assert.Contains("already exists", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Push_WithFits_ConfirmsBeforeSharing_AndDeclineSharesNothing()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, OwnerA, "Pilot A");

        var source = LocalClient(instance.Services, OwnerA);
        await BuildSourceAsync(source);   // Armor doctrine with Guardian + Scimitar
        var info = (await source.ListAsync()).Single();

        recording.OnConfirm = (_, _) => Task.FromResult(false);   // user declines the opsec prompt

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        var row = new CompositionRowViewModel(info, "Pilot A", canEdit: true, isLocal: true, source);

        await vm.PushCompositionCommand.ExecuteAsync(row);

        // The prompt was shown, named the fit count, and the decline aborted before any server interaction.
        Assert.Equal("Share fits with a server?", recording.LastConfirmTitle);
        Assert.Contains("2 fits", recording.LastConfirmMessage);
        Assert.Contains("cancelled", vm.StatusMessage);
        Assert.Null(recording.LastMessage);   // never reached the server-coupling step
    }

    [AvaloniaFact]
    public async Task Push_WithFits_Accepted_ProceedsPastTheGate()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, OwnerA, "Pilot A");

        var source = LocalClient(instance.Services, OwnerA);
        await BuildSourceAsync(source);
        var info = (await source.ListAsync()).Single();

        recording.OnConfirm = (_, _) => Task.FromResult(true);   // user accepts → push continues

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        var row = new CompositionRowViewModel(info, "Pilot A", canEdit: true, isLocal: true, source);

        await vm.PushCompositionCommand.ExecuteAsync(row);

        // Confirm was shown and accepted; with no coupled server in the test it proceeds to the coupling notice.
        Assert.Equal("Share fits with a server?", recording.LastConfirmTitle);
        Assert.Contains("Not coupled to any server", recording.LastMessage);
    }

    [AvaloniaFact]
    public async Task Push_FitlessComposition_SharesNothing_SoNoPrompt()
    {
        var recording = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, OwnerA, "Pilot A");

        var source = LocalClient(instance.Services, OwnerA);
        var (_, _, _) = await source.CreateAsync("Empty doctrine", null);   // header only, no fits
        var info = (await source.ListAsync()).Single();

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        var row = new CompositionRowViewModel(info, "Pilot A", canEdit: true, isLocal: true, source);

        await vm.PushCompositionCommand.ExecuteAsync(row);

        Assert.Null(recording.LastConfirmTitle);   // nothing to share → no opsec prompt
        Assert.Contains("Not coupled to any server", recording.LastMessage);
    }
}
