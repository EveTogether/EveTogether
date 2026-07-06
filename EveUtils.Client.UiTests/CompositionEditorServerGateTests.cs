using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// opsec gate: saving a composition onto a server-backed library shares a full self-contained copy of
/// each newly added fit with the server, the same exposure as a push (`e67224e`). The editor now confirms the fit-share
/// before persisting — only for a server target that adds fits; a local save or a metadata-only edit shares nothing and
/// prompts nothing. <see cref="RecordingDialogService.ConfirmAsync"/> throws if called with no OnConfirm set, so the
/// no-prompt cases fail loudly if the gate wrongly prompts.
/// </summary>
public class CompositionEditorServerGateTests
{
    private const int Owner = 95500001;

    private static async Task SeedAsync(IServiceProvider services)
    {
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Pilot", Owner));
        await services.GetRequiredService<IFittingRepository>().UpsertAsync(new LocalFitting
        {
            OwnerId = Owner.ToString(), EsiFittingId = 11987, Name = "Guardian — Armor", ShipTypeId = 11987,
            RawJson = "{}", ContentHash = "h-guardian", ImportedAt = DateTimeOffset.UtcNow
        });
    }

    private static async Task AddRoleWithFitAsync(CompositionEditorViewModel editor)
    {
        editor.AddRoleGroupCommand.Execute(null);
        var role = editor.Roles[0];
        role.RoleName = "Logistics";
        await editor.AddFitCommand.ExecuteAsync(role);
    }

    private static RecordingDialogService SelectingPicker() => new()
    {
        OnShowFitPicker = async picker =>
        {
            await picker.EnsureLoadedAsync();
            foreach (var row in picker.Rows)
                row.IsSelected = true;
            return picker.SelectedFits();
        }
    };

    [AvaloniaFact]
    public async Task Save_ToServer_WithFits_Declined_SharesNothing()
    {
        var recording = SelectingPicker();
        recording.OnConfirm = (_, _) => Task.FromResult(false);   // decline the share
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedAsync(instance.Services);

        var client = new RecordingCompositionClient(sharesFitsToServer: true);
        var editor = CompositionEditorViewModel.ForNew(instance.Services, client);
        editor.Name = "Armor doctrine";
        await AddRoleWithFitAsync(editor);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(recording.LastConfirmTitle);       // the gate prompted
        Assert.Equal(0, client.CreateCount);              // and nothing was persisted/shared
        Assert.Empty(client.AddedFits);
        Assert.Contains("cancelled", editor.Status);
    }

    [AvaloniaFact]
    public async Task Save_ToServer_WithFits_Accepted_Persists()
    {
        var recording = SelectingPicker();
        recording.OnConfirm = (_, _) => Task.FromResult(true);   // accept the share
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedAsync(instance.Services);

        var client = new RecordingCompositionClient(sharesFitsToServer: true);
        var editor = CompositionEditorViewModel.ForNew(instance.Services, client);
        editor.Name = "Armor doctrine";
        await AddRoleWithFitAsync(editor);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(recording.LastConfirmTitle);
        Assert.Equal(1, client.CreateCount);
        Assert.Single(client.AddedFits);
    }

    [AvaloniaFact]
    public async Task Save_ToServer_NoNewFits_DoesNotPrompt()
    {
        var recording = SelectingPicker();   // OnConfirm left unset → ConfirmAsync would throw if the gate prompted
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedAsync(instance.Services);

        var client = new RecordingCompositionClient(sharesFitsToServer: true);
        var editor = CompositionEditorViewModel.ForNew(instance.Services, client);
        editor.Name = "Empty doctrine";
        editor.AddRoleGroupCommand.Execute(null);   // a role, but no fit entries
        editor.Roles[0].RoleName = "Logistics";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(recording.LastConfirmTitle);   // no fits → no prompt
        Assert.Equal(1, client.CreateCount);
        Assert.Empty(client.AddedFits);
    }

    [AvaloniaFact]
    public async Task Save_ToLocal_WithFits_DoesNotPrompt()
    {
        var recording = SelectingPicker();   // OnConfirm unset → throws if the gate wrongly prompts on a local save
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedAsync(instance.Services);

        var client = new RecordingCompositionClient(sharesFitsToServer: false);
        var editor = CompositionEditorViewModel.ForNew(instance.Services, client);
        editor.Name = "Local doctrine";
        await AddRoleWithFitAsync(editor);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(recording.LastConfirmTitle);   // local save shares nothing → no prompt
        Assert.Equal(1, client.CreateCount);
        Assert.Single(client.AddedFits);
    }
}
