using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
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
        var toasts = services.GetService<IToastService>();

        // Every outcome below — success, rejection, cancellation, no connection — goes through here so none of
        // them can vanish silently: the status sink (a window's own status text, when the caller has one) and a
        // toast (visible regardless of which window triggered the share, or whether it is still open) share the
        // same message, never two different ones.
        void Report(string message, ToastKind kind = ToastKind.Warning, string title = "Share to server")
        {
            request.ReportStatus(message);
            toasts?.Show(title, message, kind);
        }

        // Need the raw ESI JSON to share — look the fit up by id (owner-independent).
        var repo = services.GetRequiredService<IFittingRepository>();
        var local = await repo.FindByIdAsync(request.FitId);
        if (local is null) { Report("Fit not found locally."); return; }

        // pick from ALL coupled servers, regardless of which character owns the fit.
        var sessionStore = services.GetRequiredService<IClientSessionStore>();
        var servers = await sessionStore.ListServersAsync();
        if (servers.Count == 0) { Report("Not coupled to any server — couple a character first."); return; }

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
            if (chosen is null) { Report("Share cancelled.", ToastKind.Information); return; }
            targetAddress = chosen;
        }

        var busConnector = services.GetService<IRemoteBusConnector>();
        if (busConnector?.StateFor(targetAddress) != ServerConnectionState.Connected)
        {
            Report("Not connected to that server.");
            return;
        }

        // share as which coupled character on that server (the "shared by" identity + the session used). With
        // exactly one coupled character there is nothing to ask — but that character's id (not 0) still has to
        // travel as the acting identity, and its name is worth naming in the result.
        var coupled = await sessionStore.LoadAllAsync(targetAddress);
        int shareAs;
        string? sharedAsName;
        if (coupled.Count > 1)
        {
            var charOptions = coupled
                .Select(s => new CharacterPickOption(s.CharacterId, s.CharacterName, "coupled", Enabled: true))
                .ToList();
            var picked = await dialogs.PickCharacterAsync($"Share '{local.Name}' as which character?", charOptions);
            if (picked is null) { Report("Share cancelled.", ToastKind.Information); return; }
            (shareAs, sharedAsName) = ResolveShareIdentity(coupled, picked);
        }
        else
        {
            (shareAs, sharedAsName) = ResolveShareIdentity(coupled, null);
        }

        request.ReportStatus($"Sharing '{local.Name}' via server…");
        var fitShare = services.GetRequiredService<ServerFitShareClient>();
        var (accepted, message) = await fitShare.ShareAsync(
            targetAddress, local.EsiFittingId, local.Name, local.ShipTypeId, local.RawJson, shareAs);

        if (accepted)
        {
            var resultMessage = sharedAsName is null ? $"'{local.Name}' shared." : $"'{local.Name}' shared as {sharedAsName}.";
            Report(resultMessage, ToastKind.Success, "Fit shared");
        }
        else
        {
            Report($"Share rejected: {message}", ToastKind.Error, "Share rejected");
        }

        // Refresh the matching server tab so the shared fit shows up. The seam has no tab state, so the
        // caller that owns one wires it via OnSharedToServer.
        if (accepted && request.OnSharedToServer is not null)
            await request.OnSharedToServer(targetAddress);
    }

    /// <summary>
    /// Resolves the "shared by" identity for a share: the picked character when the user chose one, otherwise the
    /// single coupled character when there is exactly one (id 0 would silently mis-attribute the share), otherwise
    /// unresolved (0, no name) — a coupled-character list of 0 shouldn't happen once the caller reached this point,
    /// but stays inert rather than guessing.
    /// </summary>
    internal static (int ShareAs, string? SharedAsName) ResolveShareIdentity(
        IReadOnlyList<ClientSessionTokens> coupled, int? picked)
    {
        if (picked is { } id)
            return (id, coupled.FirstOrDefault(s => s.CharacterId == id)?.CharacterName);
        return coupled.Count == 1 ? (coupled[0].CharacterId, coupled[0].CharacterName) : (0, null);
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
