using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using EveUtils.Client.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.Clipboard;

/// <summary>Shows what the SDE knows about a copied cosmic signature or anomaly (ET-79). One fully-scanned combat
/// site is the exception: that starts its run outright, without a card and without taking the keyboard (ET-158).</summary>
public sealed class ClipboardSignatureOffer : ISingletonService, IDisposable
{
    public const string FeatureName = "Signature detection";

    private readonly IToastService _toasts;
    private readonly ISdeAccessor _sde;
    private readonly IDialogService _dialogs;

    // Only here because ActivityWindowViewModel's constructor asks for one; a factory is the upgrade once a second
    // caller needs the same thing.
    private readonly IServiceProvider _services;
    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;

    private string? _openFingerprint;

    public ClipboardSignatureOffer(ClipboardWatchService clipboardWatch, IToastService toasts, ISdeAccessor sde,
        IDialogService dialogs, IServiceProvider services)
    {
        _toasts = toasts;
        _sde = sde;
        _dialogs = dialogs;
        _services = services;
        _subscription = clipboardWatch.Subscribe(FeatureName, OnCapture);
    }

    public void Dispose() => _subscription.Dispose();

    private void OnCapture(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Signature)
            return;

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Text)));
        lock (_gate)
        {
            // Same suppress-while-open rule as ClipboardFitImportOffer: only while this exact copy's own card is
            // still up, so a fresh copy of the same signatures after the card is gone is a new question again.
            if (_openFingerprint == fingerprint)
                return;

            _openFingerprint = fingerprint;
        }

        var rows = ClipboardSignatureParser.Parse(capture.Text);

        // Exactly one fully-scanned row is the whole case this feature can act on: half-scanned has no site, and 2+
        // rows is a menu. That one case now goes straight to a run (ET-158) instead of offering a button; the pilot
        // is in EVE, where an in-window toast is not visible anyway.
        var recognised = rows.Where(row => IsActivitySite(row.Group) && row.Name is not null).ToList();
        if (recognised is [{ } row])
        {
            StartRun(row);
            return; // no card at all: the run coming up on the copied site is the confirmation
        }

        _toasts.Show("Signature copied", BuildMessage(rows), ToastKind.Information,
            [new ToastAction("Close", () => CloseOffer(fingerprint))], () => CloseOffer(fingerprint), FeatureName);
    }

    // Wormholes are excluded for now. This still matches the group text literally in English — SiteNameAlias (ET-79)
    // covers site names, not the scan-window group column.
    private static bool IsActivitySite(string? siteType) => siteType?.EndsWith("Combat Site", StringComparison.Ordinal) is true;

    // _openFingerprint is deliberately left standing here, unlike the card path: it is what stops a second change
    // notification for the same copy from starting a second run. ponytail: an identical re-copy is therefore ignored
    // until something else is copied — drop the guard on a window-closed signal if that ever bites.
    //
    // The clipboard watch calls this on the UI thread, and the answer is awaited before anything is shown, so the
    // task is loose rather than fire-and-forget in spirit: everything it can throw is caught inside.
    private void StartRun(ClipboardSignatureRow row) => _ = _StartRunAsync(row);

    /// <summary>
    /// Ask whose run this is BEFORE the window opens, then hand the answer over. With two clients up the run window
    /// used to come up first and empty — "no character yet", "not started", "no fit: the run has no character yet" —
    /// and the question appeared beside it a moment later (Raymond, 2026-09-04). ET-158 got that question for free
    /// by leaning on START's own <c>_ResolveCharacterAsync(mayAsk: true)</c>, which can only run once the window is
    /// loaded; this is the shape the grooming pointed at instead, and the one
    /// <c>FleetRunWindowPresenter._AcceptAsync</c> has had all along.
    ///
    /// Nothing about the window's own order changes: <see cref="ActivityWindowViewModel.UseCharacter"/> only puts
    /// the answer where <c>_ResolveCharacterAsync</c> already looks first, so <c>LoadAsync</c> and the
    /// <c>RefreshFleetCommandAsync</c>-before-<c>_StoreRunAsync</c> ordering are untouched — a fleet run is still
    /// written as a fleet run.
    /// </summary>
    private async Task _StartRunAsync(ClipboardSignatureRow row)
    {
        try
        {
            // The pilots who could be flying this site: the same InGameCharacters rule the run window's own START
            // question uses. One is not a question. None means we cannot tell, and then START asks later over the
            // whole list rather than this guessing on its behalf.
            var registry = _services.GetService<ICharacterRegistry>();
            List<Character> flying = registry is null
                ? []
                : [.. InGameCharacters.Among(await registry.GetAllAsync(), _services.GetService<ILocalCharacterPresence>())];

            Character? pilot = flying is [{ } only] ? only : null;
            var startsOnArrival = true;

            // A window already up that knows its pilot has been asked this once, and copying a site is not a reason
            // to ask again: the answer would be the same, and the asking is a modal dialog taking the keyboard off
            // EVE — the one thing ET-158 exists to avoid (Raymond, 2026-09-04). A window WITHOUT a pilot is still a
            // fair question, which is why this reads the pilot rather than "is a window open".
            //
            // ponytail: this cannot tell "the same pilot carries on" from "he switched clients", because a clipboard
            // copy carries no sender — Windows does not say which process copied, and the payload holds no pilot
            // name. A copy made on a second client while the window is for the first is therefore filed under the
            // first. That was already true before the question moved forward; giving the copy an owner is the open
            // question from the 2026-09-02 analysis and wants the foreground EVE window, not a guess here.
            bool answeredAlready = _dialogs.ActivityWindowPilot is not null;

            if (pilot is null && flying.Count > 1 && !answeredAlready)
            {
                int? picked = await _dialogs.PickCharacterAsync("Whose run is this?",
                    [.. flying.Select(character => new CharacterPickOption(
                        character.EsiCharacterId!.Value, character.Name, "EVE client running", Enabled: true))]);
                pilot = flying.FirstOrDefault(character => character.EsiCharacterId == picked);

                // Dismissed is not "throw the copy away": the window still comes up on the site he copied, it just
                // does not start itself. START is the way back to this same question — asking it again is exactly
                // what _ResolveCharacterAsync(mayAsk: true) does — so a stray Escape costs a click, not the scan.
                startsOnArrival = pilot is not null;
            }

            var window = new ActivityWindowViewModel(ActivityKind.Site, _services)
            {
                // The scan id travels with the site: two Sansha Refuges scanned an hour apart are two runs, and only
                // this tells them apart.
                SignatureId = row.SignatureId,
                SignatureGroup = row.Group,
                SignatureName = row.Name,
                MatchedSites = MatchSites(row.Name!),
                StartsOnArrival = startsOnArrival
            };
            // Before the window is shown, so nothing it loads has to ask again.
            if (pilot is { EsiCharacterId: { } characterId })
                window.UseCharacter(characterId, pilot.Name);

            _dialogs.ShowActivityWindow(window, RunWindowOpenTrigger.CopiedSignature);
        }
        catch (Exception ex)
        {
            // The only caller is a clipboard subscription returning void, so an escape here is an unobserved task.
            _toasts.Show("Run not started", $"Could not open the run on {row.Name}: {ex.Message}", ToastKind.Error);
        }
    }

    private void CloseOffer(string fingerprint)
    {
        lock (_gate)
        {
            if (_openFingerprint == fingerprint)
                _openFingerprint = null;
        }
    }

    private string BuildMessage(IReadOnlyList<ClipboardSignatureRow> rows)
    {
        var lines = new List<string>();
        var notFullyScanned = 0;

        foreach (var row in rows)
        {
            if (row.Name is null)
            {
                notFullyScanned++;
                continue;
            }

            lines.Add(DescribeSignature(row.SignatureId, row.Name));
        }

        // ET-79 AC-6: an unscanned row never gets its own line, and it is never dropped either — it is always
        // accounted for in this one trailing count, however many there are.
        if (notFullyScanned > 0)
            lines.Add($"{notFullyScanned} more not fully scanned yet");

        return string.Join('\n', lines);
    }

    private string DescribeSignature(string signatureId, string name)
    {
        var matches = MatchSites(name);
        if (matches.Count == 0)
            return $"{signatureId} · {name} — not in the site catalogue";

        var suffix = SdeSiteDescription.DescribeMatches(matches);
        return suffix.Length == 0 ? $"{signatureId} · {name}" : $"{signatureId} · {name} — {suffix}";
    }

    /// <summary>The one route from a copied site name into the catalogue — the toast and the window it opens must
    /// not answer differently. Exact match across every SDE locale (ET-79 AC-4); a miss does not prove the site is
    /// missing (Data Site, Relic Site and Wormhole are not in the catalogue at all), so neither caller says it is.</summary>
    private IReadOnlyList<SdeSite> MatchSites(string name) => _sde.FindSitesByExactName(name);

}
