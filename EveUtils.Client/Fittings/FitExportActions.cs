using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Fittings;

/// <summary>
/// Shared implementation of the four fit export actions. The bodies are lifted from the former
/// <c>MainWindowViewModel.PushFitting</c>/<c>ShareFitting</c>/<c>ExportFitting</c> so the Local tab keeps behaving
/// exactly as before; <see cref="CopyEveshipLinkAsync"/> is new (a direct clipboard copy, previously only reachable
/// inside the EFT window).
///
/// The seam is stateless: every collaborator is resolved from the root <see cref="IServiceProvider"/> per call —
/// mirroring how the view-model resolved them — and the per-call view-model state arrives in the request.
/// </summary>
public sealed class FitExportActions(IServiceProvider services) : IFitExportActions, ISingletonService
{
    public async Task PushToEveAsync(FitExportRequest request)
    {
        var dialogs = services.GetRequiredService<IDialogService>();

        var charId = await dialogs.PickCharacterAsync(
            $"Push '{request.FitName}' to which character?",
            request.PickOptionsFor(FittingsScopeCatalog.WriteFittings));
        if (charId is null) { request.ReportStatus("Push cancelled."); return; }

        var tokenStore = services.GetRequiredService<IPerCharacterTokenStore>();
        var tokens = await tokenStore.LoadAsync(charId.Value);
        if (tokens is null) { request.ReportStatus("No token for that character — sign in first."); return; }

        request.ReportStatus($"Pushing '{request.FitName}' to EVE…");
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var result = await dispatcher.Send(new PushFittingToEsiCommand(charId.Value, tokens.AccessToken, request.FitId));
        request.ReportStatus(result.IsSuccess
            ? $"Pushed '{request.FitName}' → ESI id {result.Value}."
            : $"Push failed: {result.Messages.FirstOrDefault()?.Text}");
    }

    public async Task ShareToServerAsync(FitExportRequest request)
    {
        var dialogs = services.GetRequiredService<IDialogService>();

        // Need the raw ESI JSON to share — look the fit up by id (owner-independent).
        var repo = services.GetRequiredService<IFittingRepository>();
        var local = await repo.FindByIdAsync(request.FitId);
        if (local is null) { request.ReportStatus("Fit not found locally."); return; }

        // pick from ALL coupled servers, regardless of which character owns the fit.
        var sessionStore = services.GetRequiredService<IClientSessionStore>();
        var servers = await sessionStore.ListServersAsync();
        if (servers.Count == 0) { request.ReportStatus("Not coupled to any server — couple a character first."); return; }

        var serverRegistry = services.GetService<IServerRegistry>();
        string targetAddress;
        if (servers.Count == 1)
        {
            targetAddress = servers[0];
        }
        else
        {
            var options = new List<ServerPickOption>();
            foreach (var addr in servers)
                options.Add(new ServerPickOption(addr,
                    serverRegistry is null ? addr : await serverRegistry.DisplayNameAsync(addr)));
            var chosen = await dialogs.SelectServerAsync($"Share '{local.Name}' to which server?", options);
            if (chosen is null) { request.ReportStatus("Share cancelled."); return; }
            targetAddress = chosen;
        }

        var busConnector = services.GetService<IRemoteBusConnector>();
        if (busConnector?.StateFor(targetAddress) != ServerConnectionState.Connected)
        {
            request.ReportStatus("Not connected to that server.");
            return;
        }

        // share as which coupled character on that server (the "shared by" identity + the session used).
        var shareAs = 0;
        var coupled = await sessionStore.LoadAllAsync(targetAddress);
        if (coupled.Count > 1)
        {
            var charOptions = coupled
                .Select(s => new CharacterPickOption(s.CharacterId, s.CharacterName, "coupled", Enabled: true))
                .ToList();
            var picked = await dialogs.PickCharacterAsync($"Share '{local.Name}' as which character?", charOptions);
            if (picked is null) { request.ReportStatus("Share cancelled."); return; }
            shareAs = picked.Value;
        }

        request.ReportStatus($"Sharing '{local.Name}' via server…");
        var fitShare = services.GetRequiredService<ServerFitShareClient>();
        var (accepted, message) = await fitShare.ShareAsync(
            targetAddress, local.EsiFittingId, local.Name, local.ShipTypeId, local.RawJson, shareAs);
        request.ReportStatus(accepted ? $"'{local.Name}' shared." : $"Share rejected: {message}");

        // Refresh the matching server tab so the shared fit shows up. The seam has no tab state, so the
        // caller that owns one wires it via OnSharedToServer.
        if (accepted && request.OnSharedToServer is not null)
            await request.OnSharedToServer(targetAddress);
    }

    public async Task CopyEveshipLinkAsync(FitExportRequest request)
    {
        var esiFit = await LoadFitModelAsync(request);
        if (esiFit is null) return;

        var url = services.GetRequiredService<IFitExporter>().ToEveshipUrl(esiFit);
        await services.GetRequiredService<IDialogService>().SetClipboardTextAsync(url);
        request.ReportStatus($"Copied eveship.fit link for '{esiFit.Name}'.");
    }

    public async Task OpenEftWindowAsync(FitExportRequest request)
    {
        var esiFit = await LoadFitModelAsync(request);
        if (esiFit is null) return;

        var exporter = services.GetRequiredService<IFitExporter>();
        await services.GetRequiredService<IDialogService>().ShowFitExportAsync(
            esiFit.Name, exporter.ToEft(esiFit), exporter.ToDna(esiFit), exporter.ToEveshipUrl(esiFit));
    }

    /// <summary>Loads + deserializes the stored fit; reports a status and returns null on a missing/unreadable fit.</summary>
    private async Task<EsiFitting?> LoadFitModelAsync(FitExportRequest request)
    {
        var local = await services.GetRequiredService<IFittingRepository>().FindByIdAsync(request.FitId);
        if (local is null) { request.ReportStatus("Fit not found."); return null; }

        EsiFitting? esiFit;
        try { esiFit = JsonSerializer.Deserialize<EsiFitting>(local.RawJson); }
        catch { esiFit = null; }
        if (esiFit is null) { request.ReportStatus("Could not read that fit."); return null; }
        return esiFit;
    }
}
