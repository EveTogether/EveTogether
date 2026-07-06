using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// View-model + render checks for the composition editor: building a new composition (role group + fits picked
/// through the reusable picker) persists the graph through the composition client; editing an existing one diffs against the
/// loaded snapshot and replays only the changed roles/entries; the library wires open → editor → reload; and both new
/// dialogs render headlessly (Iron Law #9).
/// </summary>
public class CompositionEditorTests
{
    private const int Owner = 95400001;

    private static async Task SeedCharacterAsync(IServiceProvider services, int characterId, string name) =>
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));

    private static async Task SeedLocalFitAsync(IServiceProvider services, int owner, string name, int shipTypeId, string contentHash) =>
        await services.GetRequiredService<IFittingRepository>().UpsertAsync(new LocalFitting
        {
            OwnerId = owner.ToString(), EsiFittingId = shipTypeId, Name = name, ShipTypeId = shipTypeId,
            RawJson = "{}", ContentHash = contentHash, ImportedAt = DateTimeOffset.UtcNow
        });

    private static async Task<long> SeedCompositionAsync(IServiceProvider services, int owner, string name, int? groupMin, params string[] fits)
    {
        var repo = services.GetRequiredService<IFleetCompositionRepository>();
        var now = DateTimeOffset.UtcNow;
        var compositionId = await repo.AddAsync(new FleetComposition { Name = name, OwnerCharacterId = owner, IsClientOnly = true, CreatedAt = now, UpdatedAt = now });
        var roleId = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "Logistics", GroupMinCount = groupMin, SortOrder = 0 });
        var order = 0;
        foreach (var fit in fits)
            await repo.AddEntryAsync(new FleetCompositionEntry
            {
                RoleId = roleId,
                Fit = new FitReference { ShipTypeId = 11987 + order, FitName = fit, RawJson = "{}", ContentHash = fit + order },
                SortOrder = order++
            });
        return compositionId;
    }

    private static LocalFleetCompositionClient LocalClient(IServiceProvider services, int owner) =>
        new(services.GetRequiredService<ClientFleetService>(),
            services.GetRequiredService<IFleetCompositionRepository>(), owner);

    [AvaloniaFact]
    public async Task BuildNewComposition_PicksFits_AndPersistsGraph()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedLocalFitAsync(instance.Services, Owner, "Guardian — Armor", 11987, "h-guardian");
        await SeedLocalFitAsync(instance.Services, Owner, "Scimitar — Shield", 11978, "h-scimitar");

        recording.OnShowFitPicker = async picker =>
        {
            await picker.EnsureLoadedAsync();
            foreach (var row in picker.Rows)
                row.IsSelected = true;
            return picker.SelectedFits();
        };

        var client = LocalClient(instance.Services, Owner);
        var editor = CompositionEditorViewModel.ForNew(instance.Services, client);
        editor.Name = "Armor doctrine";
        editor.AddRoleGroupCommand.Execute(null);
        var role = editor.Roles[0];
        role.RoleName = "Logistics";
        role.GroupMinText = "5";
        await editor.AddFitCommand.ExecuteAsync(role);

        // Live summary reflects the working copy before save.
        Assert.Equal(2, role.Entries.Count);
        Assert.Equal(1, editor.RoleCount);
        Assert.Equal(2, editor.FitCount);
        Assert.Equal(5, editor.MinPilots);

        await editor.SaveCommand.ExecuteAsync(null);

        var info = Assert.Single(await client.ListAsync());
        Assert.Equal("Armor doctrine", info.Name);
        var detail = await client.GetAsync(info.Id);
        var savedRole = Assert.Single(detail!.Roles);
        Assert.Equal("Logistics", savedRole.RoleName);
        Assert.Equal(5, savedRole.GroupMinCount);
        Assert.Equal(2, savedRole.Entries.Count);
    }

    [AvaloniaFact]
    public async Task EditExisting_DiffsAndReplays_RenameRemoveAndAdd()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedLocalFitAsync(instance.Services, Owner, "Eagle — Rail", 11978, "h-eagle");

        var client = LocalClient(instance.Services, Owner);
        var compositionId = await SeedCompositionAsync(instance.Services, Owner, "Armor doctrine", groupMin: 5, "Guardian", "Scimitar");
        var detail = await client.GetAsync(compositionId);

        recording.OnShowFitPicker = async picker =>
        {
            await picker.EnsureLoadedAsync();
            var eagle = picker.Rows.First(r => r.FitName.Contains("Eagle"));
            eagle.IsSelected = true;
            return picker.SelectedFits();
        };

        var editor = CompositionEditorViewModel.ForExisting(instance.Services, client, detail!);
        var role = editor.Roles[0];
        role.RoleName = "Logi";
        role.GroupMinText = "6";
        editor.RemoveEntryCommand.Execute(role.Entries.First(e => e.FitName == "Guardian"));
        await editor.AddFitCommand.ExecuteAsync(role);

        await editor.SaveCommand.ExecuteAsync(null);

        var after = await client.GetAsync(compositionId);
        var savedRole = Assert.Single(after!.Roles);
        Assert.Equal("Logi", savedRole.RoleName);
        Assert.Equal(6, savedRole.GroupMinCount);
        Assert.Equal(2, savedRole.Entries.Count);
        Assert.Contains(savedRole.Entries, e => e.Fit.FitName == "Eagle — Rail");
        Assert.DoesNotContain(savedRole.Entries, e => e.Fit.FitName == "Guardian");
    }

    [AvaloniaFact]
    public async Task OpenComposition_RunsEditor_PersistsAndReloads()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedCompositionAsync(instance.Services, Owner, "Armor doctrine", groupMin: 5, "Guardian");

        recording.OnShowCompositionEditor = async editor =>
        {
            editor.Name = "Armor doctrine v2";
            await editor.SaveCommand.ExecuteAsync(null);
            return true;
        };

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        var row = Assert.Single(vm.SelectedTab!.Compositions);

        await vm.OpenCompositionCommand.ExecuteAsync(row);

        Assert.NotNull(recording.LastCompositionEditor);
        Assert.Equal("Armor doctrine v2", Assert.Single(vm.SelectedTab!.Compositions).Name);
    }

    [AvaloniaFact]
    public async Task OpenFitDetail_DeserialisesSnapshot_AndShowsDetail()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();
        var now = DateTimeOffset.UtcNow;
        var compositionId = await repo.AddAsync(new FleetComposition { Name = "Armor", OwnerCharacterId = Owner, IsClientOnly = true, CreatedAt = now, UpdatedAt = now });
        var roleId = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "Logistics", GroupMinCount = 1, SortOrder = 0 });
        var rawJson = JsonSerializer.Serialize(new EsiFitting(0, "Guardian — Armor", "", 11987, new List<EsiFittingItem>()));
        await repo.AddEntryAsync(new FleetCompositionEntry
        {
            RoleId = roleId,
            Fit = new FitReference { ShipTypeId = 11987, FitName = "Guardian — Armor", RawJson = rawJson, ContentHash = "h" },
            SortOrder = 0
        });

        var client = LocalClient(instance.Services, Owner);
        var editor = CompositionEditorViewModel.ForExisting(instance.Services, client, (await client.GetAsync(compositionId))!);
        var entry = editor.Roles[0].Entries[0];

        await editor.OpenFitDetailCommand.ExecuteAsync(entry);

        Assert.NotNull(recording.LastFitDetail);
        Assert.Equal("Guardian — Armor", recording.LastFitDetail!.Name);
    }

    [AvaloniaFact]
    public async Task FitPickerWindow_Renders()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedLocalFitAsync(instance.Services, Owner, "Guardian — Armor", 11987, "h-guardian");
        await SeedLocalFitAsync(instance.Services, Owner, "Scimitar — Shield", 11978, "h-scimitar");

        var vm = new FitPickerViewModel(instance.Services);
        await vm.EnsureLoadedAsync();
        vm.Rows[0].IsSelected = true;

        var window = new FitPickerWindow(vm) { Width = 560, Height = 580 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-picker.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task CompositionEditorWindow_Renders()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        var client = LocalClient(instance.Services, Owner);
        var compositionId = await SeedCompositionAsync(instance.Services, Owner, "Homefront Vanguard", groupMin: 40, "Megathron", "Hyperion");
        var detail = await client.GetAsync(compositionId);

        var editor = CompositionEditorViewModel.ForExisting(instance.Services, client, detail!);

        var window = new CompositionEditorWindow(editor) { Width = 600, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-composition-editor.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ForView_IsReadOnly_ShowsGraph_AndSaveDoesNotPersist()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        var client = LocalClient(instance.Services, Owner);
        var compositionId = await SeedCompositionAsync(instance.Services, Owner, "Armor doctrine", groupMin: 5, "Guardian", "Scimitar");

        var view = CompositionEditorViewModel.ForView(instance.Services, client, (await client.GetAsync(compositionId))!);

        Assert.True(view.IsReadOnly);
        Assert.False(view.IsEditable);
        Assert.Equal("View composition", view.Title);
        Assert.Equal("CLOSE", view.CancelButtonLabel);
        Assert.Equal(2, view.Roles[0].Entries.Count);   // the doctrine is shown read-only

        // Save is a no-op in read-only: editing the name and saving must not persist.
        view.Name = "Tampered";
        await view.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Armor doctrine", (await client.GetAsync(compositionId))!.Composition.Name);
    }

    [AvaloniaFact]
    public async Task OpenComposition_NonEditableRow_OpensReadOnlyView()
    {
        var recording = new RecordingDialogService { OnShowCompositionEditor = _ => Task.FromResult(false) };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        var client = LocalClient(instance.Services, Owner);
        var compositionId = await SeedCompositionAsync(instance.Services, Owner, "Other's doctrine", groupMin: 5, "Guardian");

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();

        // A row the acting character may not edit (someone else's) routes to the read-only view, not the editor.
        var info = new FleetCompositionInfo(compositionId, "Other's doctrine", null, 99, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CanEdit: false, OwnerName: "Other");
        var row = new CompositionRowViewModel(info, "Other", canEdit: false, isLocal: true, client);
        await vm.OpenCompositionCommand.ExecuteAsync(row);

        Assert.NotNull(recording.LastCompositionEditor);
        Assert.True(recording.LastCompositionEditor!.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task CompositionEditorWindow_ReadOnly_Renders()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        var client = LocalClient(instance.Services, Owner);
        var compositionId = await SeedCompositionAsync(instance.Services, Owner, "Homefront Vanguard", groupMin: 40, "Megathron", "Hyperion");

        var view = CompositionEditorViewModel.ForView(instance.Services, client, (await client.GetAsync(compositionId))!);

        var window = new CompositionEditorWindow(view) { Width = 600, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-composition-readonly.png");
        window.Close();
    }
}
