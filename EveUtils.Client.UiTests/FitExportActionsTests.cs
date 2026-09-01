using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fittings;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// the shared <see cref="IFitExportActions"/> seam (the four export actions lifted out of
/// MainWindowViewModel). Drives the real client DI on a throwaway instance (so the SQLite fit repo is real) with a
/// <see cref="RecordingDialogService"/> standing in for the UI, and asserts the status text + the dialog/clipboard
/// effects of each action. Network paths (a real ESI push / gRPC share) aren't exercised here — only the local
/// decision branches that need no server.
/// </summary>
public class FitExportActionsTests
{
    private static async Task<int> SeedFitAsync(IServiceProvider services, string name = "Test Rifter", int shipTypeId = 587)
    {
        var fit = new EsiFitting(0, name, "", shipTypeId, new[] { new EsiFittingItem(2185, "HiSlot0", 1) });
        var repo = services.GetRequiredService<IFittingRepository>();
        await repo.UpsertAsync(new LocalFitting
        {
            OwnerId = "1001",
            EsiFittingId = 42,
            Name = name,
            ShipTypeId = shipTypeId,
            RawJson = JsonSerializer.Serialize(fit),
            ImportedAt = DateTimeOffset.UtcNow,
            ContentHash = "hash-" + name
        });
        var all = await repo.ListAllAsync();
        return all.First(f => f.Name == name).Id;
    }

    private static (FitExportRequest Request, List<string> Statuses) BuildRequest(
        int fitId, string name = "Test Rifter",
        Func<string, IReadOnlyList<CharacterPickOption>>? pickOptions = null)
    {
        var statuses = new List<string>();
        var request = new FitExportRequest(fitId, name,
            pickOptions ?? (_ => new List<CharacterPickOption>()),
            statuses.Add);
        return (request, statuses);
    }

    [Fact]
    public async Task CopyEveshipLink_CopiesUrlToClipboard()
    {
        var dialog = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialog));
        var actions = instance.Services.GetRequiredService<IFitExportActions>();
        var fitId = await SeedFitAsync(instance.Services);

        var (request, statuses) = BuildRequest(fitId);
        await actions.CopyEveshipLinkAsync(request);

        Assert.NotNull(dialog.LastClipboardText);
        Assert.StartsWith("https://eveship.fit", dialog.LastClipboardText);
        Assert.Contains(statuses, s => s.Contains("Copied"));
    }

    [Fact]
    public async Task OpenEftWindow_ShowsExportDialogForTheFit()
    {
        var dialog = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialog));
        var actions = instance.Services.GetRequiredService<IFitExportActions>();
        var fitId = await SeedFitAsync(instance.Services);

        var (request, _) = BuildRequest(fitId);
        await actions.OpenEftWindowAsync(request);

        Assert.NotNull(dialog.LastFitExport);
        Assert.Equal("Test Rifter", dialog.LastFitExport!.Value.FitName);
        Assert.False(string.IsNullOrWhiteSpace(dialog.LastFitExport.Value.Eft));
        Assert.StartsWith("https://eveship.fit", dialog.LastFitExport.Value.EveshipUrl);
    }

    [Fact]
    public async Task PushToEve_PickedCharacterWithoutToken_AsksToSignIn()
    {
        var dialog = new RecordingDialogService { OnPickCharacter = (_, _) => Task.FromResult<int?>(123) };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialog));
        var actions = instance.Services.GetRequiredService<IFitExportActions>();
        var fitId = await SeedFitAsync(instance.Services);

        var (request, statuses) = BuildRequest(fitId,
            pickOptions: _ => new List<CharacterPickOption> { new(123, "Pilot", "local", Enabled: true) });
        await actions.PushToEveAsync(request);

        Assert.Equal("Push '" + "Test Rifter" + "' to which character?", dialog.LastPrompt);
        Assert.Contains(statuses, s => s.Contains("No token"));
    }

    [Fact]
    public async Task PushToEve_Cancelled_ReportsCancelled()
    {
        var dialog = new RecordingDialogService(); // default OnPickCharacter cancels (null)
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialog));
        var actions = instance.Services.GetRequiredService<IFitExportActions>();
        var fitId = await SeedFitAsync(instance.Services);

        var (request, statuses) = BuildRequest(fitId);
        await actions.PushToEveAsync(request);

        Assert.Contains(statuses, s => s == "Push cancelled.");
    }

    [Fact]
    public async Task ShareToServer_NoCoupledServer_ReportsNotCoupled()
    {
        var dialog = new RecordingDialogService();
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IDialogService>(dialog);
            s.AddSingleton<IToastService>(toasts);
        });
        var actions = instance.Services.GetRequiredService<IFitExportActions>();
        var fitId = await SeedFitAsync(instance.Services);

        var (request, statuses) = BuildRequest(fitId);
        await actions.ShareToServerAsync(request);

        Assert.Contains(statuses, s => s.Contains("Not coupled to any server"));
        Assert.Contains(toasts.Toasts, t => t.Message != null && t.Message.Contains("Not coupled to any server"));
    }

    [Fact]
    public async Task ShareToServer_NotConnectedToTargetServer_ReportsAndToasts()
    {
        var dialog = new RecordingDialogService();
        var toasts = new RecordingToastService();
        var busConnector = new FakeRemoteBusConnector(); // default state: Disconnected — never told to connect
        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IDialogService>(dialog);
            s.AddSingleton<IToastService>(toasts);
            s.AddSingleton<IRemoteBusConnector>(busConnector);
        });
        var actions = instance.Services.GetRequiredService<IFitExportActions>();
        var fitId = await SeedFitAsync(instance.Services);

        // One coupled character on the (unconnected) target server, so ShareToServerAsync auto-picks it as the
        // only server and reaches the connection check without a picker dialog.
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync("test-server", new ClientSessionTokens("access", "refresh", "Pilot One", 123),
            TestContext.Current.CancellationToken);

        var (request, statuses) = BuildRequest(fitId);
        await actions.ShareToServerAsync(request);

        Assert.Contains(statuses, s => s == "Not connected to that server.");
        Assert.Contains(toasts.Toasts, t => t.Message == "Not connected to that server." && t.Kind == ToastKind.Warning);
    }

    [Fact]
    public async Task MissingFit_ReportsNotFound()
    {
        var dialog = new RecordingDialogService();
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IDialogService>(dialog);
            s.AddSingleton<IToastService>(toasts);
        });
        var actions = instance.Services.GetRequiredService<IFitExportActions>();

        var (copyRequest, copyStatuses) = BuildRequest(999_999);
        await actions.CopyEveshipLinkAsync(copyRequest);
        Assert.Contains(copyStatuses, s => s == "Fit not found.");

        var (shareRequest, shareStatuses) = BuildRequest(999_999);
        await actions.ShareToServerAsync(shareRequest);
        Assert.Contains(shareStatuses, s => s == "Fit not found locally.");
        Assert.Null(dialog.LastClipboardText);
        Assert.Contains(toasts.Toasts, t => t.Message == "Fit not found locally.");
    }

    [Fact]
    public void ResolveShareIdentity_SingleCoupledCharacter_UsesItsIdNotZero()
    {
        var coupled = new List<ClientSessionTokens> { new("access", "refresh", "Solo Pilot", 42) };

        var (shareAs, name) = FitExportActions.ResolveShareIdentity(coupled, picked: null);

        Assert.Equal(42, shareAs);
        Assert.Equal("Solo Pilot", name);
    }

    [Fact]
    public void ResolveShareIdentity_PickedCharacter_ResolvesItsName()
    {
        var coupled = new List<ClientSessionTokens>
        {
            new("access", "refresh", "First Pilot", 1),
            new("access", "refresh", "Second Pilot", 2),
        };

        var (shareAs, name) = FitExportActions.ResolveShareIdentity(coupled, picked: 2);

        Assert.Equal(2, shareAs);
        Assert.Equal("Second Pilot", name);
    }

    [Fact]
    public void ResolveShareIdentity_NoCoupledCharacterAndNothingPicked_StaysUnresolved()
    {
        var (shareAs, name) = FitExportActions.ResolveShareIdentity(Array.Empty<ClientSessionTokens>(), picked: null);

        Assert.Equal(0, shareAs);
        Assert.Null(name);
    }
}
