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
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Clipboard;

/// <summary>Shows what the SDE knows about a copied cosmic signature or anomaly (ET-79). One fully-scanned site is
/// the exception: that starts its run outright, without a card and without taking the keyboard (ET-158, widened
/// past combat-only by ET-177). The catalogue only enriches what is shown — it never gates whether the run starts
/// (ET-178): Data Site, Relic Site and Wormhole are never in it at all, and that is not a reason to stay silent.
/// A wormhole is the one thing kept out, and on its SDE type id rather than on that absence — see <see cref="IsWormhole"/>.
/// Starting outright is itself an opt-out (<see cref="AutoStartSettingKey"/>, ET-264): with it off the window still
/// opens prepared — name, rewards and type filled in — and the pilot presses START himself.</summary>
public sealed class ClipboardSignatureOffer : ISingletonService, IDisposable
{
    public const string FeatureName = "Signature detection";

    /// <summary>"false" opens the window prepared instead of starting it outright (ET-264). Default on: unset or
    /// "true" keeps today's behaviour.</summary>
    public const string AutoStartSettingKey = "clipboard.signature.auto-start";

    private readonly IToastService _toasts;
    private readonly ISdeAccessor _sde;
    private readonly IDialogService _dialogs;

    // Only here because ActivityWindowViewModel's constructor asks for one; a factory is the upgrade once a second
    // caller needs the same thing.
    private readonly IServiceProvider _services;
    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;
    private readonly TimeProvider _clock;

    private string? _openFingerprint;
    private DateTimeOffset _openFingerprintAtUtc;

    // ET-264: bounds the "one copy, two notifications" guard below in time instead of holding it until something
    // else is copied. That old rule never let go of a run-starting copy at all (CloseOffer only ever cleared the
    // card path, never this one) — so a DISCARD followed by copying the exact same site again did nothing. Three
    // seconds is comfortably past the two notifications one real clipboard change can fire, and comfortably short of
    // the time a DISCARD's own confirmation dialog takes to click through.
    private static readonly TimeSpan DuplicateNotificationWindow = TimeSpan.FromSeconds(3);

    public ClipboardSignatureOffer(ClipboardWatchService clipboardWatch, IToastService toasts, ISdeAccessor sde,
        IDialogService dialogs, IServiceProvider services)
    {
        _toasts = toasts;
        _sde = sde;
        _dialogs = dialogs;
        _services = services;
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _subscription = clipboardWatch.Subscribe(FeatureName, OnCapture);
    }

    public void Dispose() => _subscription.Dispose();

    private void OnCapture(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Signature)
            return;

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Text)));
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            // Suppress-while-recent rule (ET-264): only within DuplicateNotificationWindow, so a fresh copy of the
            // same signatures after that — the card is long gone, or the run was DISCARDed — is a new question
            // again rather than silence.
            if (_openFingerprint == fingerprint && now - _openFingerprintAtUtc < DuplicateNotificationWindow)
                return;

            _openFingerprint = fingerprint;
            _openFingerprintAtUtc = now;
        }

        var rows = ClipboardSignatureParser.Parse(capture.Text);

        // Exactly one fully-scanned row is the whole case this feature can act on: half-scanned has no site, and 2+
        // rows is a menu. That one case now goes straight to a run (ET-158) instead of offering a button; the pilot
        // is in EVE, where an in-window toast is not visible anyway. The catalogue plays no part in this decision
        // (ET-178): a name it does not carry is registered on its own name, same as one it does. A wormhole drops
        // out here rather than being hidden — it still gets its line on the card, it just never becomes a run.
        var recognised = rows.Where(row => row.Name is not null && !IsWormhole(row.Name)).ToList();
        if (recognised is [{ } row])
        {
            StartRun(row, capture.CopiedByCharacter);
            return; // no card at all: the run coming up on the copied site is the confirmation
        }

        _toasts.Show("Signature copied", BuildMessage(rows), ToastKind.Information,
            [new ToastAction("Close", () => CloseOffer(fingerprint))], () => CloseOffer(fingerprint), FeatureName);
    }

    // _openFingerprint is left standing here too, unlike the card path's own CloseOffer: it is what stops a second
    // change notification for the same copy from starting a second run. It ages out on its own after
    // DuplicateNotificationWindow (ET-264) rather than staying forever — the ponytail note this used to carry ("drop
    // the guard on a window-closed signal if that ever bites") did bite, once a pilot discarded a run and copied the
    // exact same site again.
    //
    // The clipboard watch calls this on the UI thread, and the answer is awaited before anything is shown, so the
    // task is loose rather than fire-and-forget in spirit: everything it can throw is caught inside.
    private void StartRun(ClipboardSignatureRow row, string? copiedByCharacter) => _ = _StartRunAsync(row, copiedByCharacter);

    /// <summary>ET-264: "Start automatically when copied", per kind, in the existing settings window. Default on
    /// (today's behaviour) — only an explicit "false" prepares the window instead of starting it.</summary>
    private async Task<bool> _StartsAutomaticallyAsync()
    {
        if (_services.GetService<ISettingRepository>() is not { } settings)
            return true;

        foreach (ClientSetting setting in await settings.ListAsync())
            if (setting.Key == AutoStartSettingKey)
                return !string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase);
        return true;
    }

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
    private async Task _StartRunAsync(ClipboardSignatureRow row, string? copiedByCharacter)
    {
        try
        {
            // The pilots who could be flying this site: the same InGameCharacters rule the run window's own START
            // question uses. One is not a question. Seeing none is not knowing, so the known characters stand in.
            var registry = _services.GetService<ICharacterRegistry>();
            List<Character> known = registry is null
                ? []
                : (await registry.GetAllAsync()).Where(character => character.EsiCharacterId is not null).ToList();
            List<Character> flying = InGameCharacters.Among(known, _services.GetService<ILocalCharacterPresence>());
            List<Character> candidates = flying.Count == 0 ? known : flying;

            Character? pilot = candidates is [{ } only] ? only : null;
            List<Character> additional = [];
            var startsOnArrival = await _StartsAutomaticallyAsync();

            // A window already up that knows its pilot has been asked this once, and copying a site is not a reason
            // to ask again: the answer would be the same, and the asking is a modal dialog taking the keyboard off
            // EVE — the one thing ET-158 exists to avoid (Raymond, 2026-09-04). A window WITHOUT a pilot is still a
            // fair question, which is why this reads the pilot rather than "is a window open".
            //
            // ponytail: this cannot tell "the same pilot carries on" from "he switched clients", because a copy on a
            // second client while the window is for the first is filed under the first (the window already has an
            // answer, so the question below is skipped outright). ET-138 built the observation this leans on instead
            // — ClipboardCapture.CopiedByCharacter, read at notification time — and ET-211 measured it reliable
            // fourteen of fourteen times against a live client, which is what makes ET-216's preselection below safe.
            bool answeredAlready = _dialogs.ActivityWindowPilot is not null;

            if (pilot is null && candidates.Count > 1 && !answeredAlready)
            {
                // ET-216: the character whose own copy this was starts ticked in the question below — a starting
                // point, not a guess spent outright: an unmatched name (unknown sender, or a sender not among these
                // candidates) simply leaves nothing preselected, same as before this ticket.
                int? preselectedCharacterId = copiedByCharacter is null
                    ? null
                    : candidates.FirstOrDefault(character =>
                        string.Equals(character.Name, copiedByCharacter, StringComparison.OrdinalIgnoreCase))?.EsiCharacterId;

                // Multi-select (ET-210): flying this site on several of these candidates at once is as real a case
                // as flying it on one. The first ticked box is the pilot the window is for; the rest ride along as
                // their own run under the same group code once the window has one to share.
                IReadOnlyList<int>? picked = await _dialogs.PickCharactersAsync("Whose run is this?",
                    [.. candidates.Select(character => new CharacterPickOption(
                        character.EsiCharacterId!.Value, character.Name,
                        flying.Contains(character) ? "EVE client running" : "local character", Enabled: true))],
                    preselectedCharacterId is { } preselectedId ? [preselectedId] : null);
                pilot = picked is { Count: > 0 }
                    ? candidates.FirstOrDefault(character => character.EsiCharacterId == picked[0])
                    : null;
                if (picked is { Count: > 1 })
                    additional = [.. picked.Skip(1)
                        .Select(id => candidates.FirstOrDefault(character => character.EsiCharacterId == id))
                        .Where(character => character is not null)
                        .Select(character => character!)];

                // Dismissed is not "throw the copy away": the window still comes up on the site he copied, it just
                // does not start itself. START is the way back to this same question over the same candidate set.
                startsOnArrival = startsOnArrival && pilot is not null;
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
            if (additional.Count > 0)
                window.UseAdditionalCharacters(
                    [.. additional.Select(character => (character.EsiCharacterId!.Value, character.Name))]);

            _dialogs.ShowActivityWindow(window, RunWindowOpenTrigger.CopiedFromClipboard);
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

    // ET-178 AC-3: a catalogue miss says nothing here — same as a match with nothing to add (ET-79 AC-6's silence
    // extended to the whole line instead of just the missing fields). The name itself already says what was copied.
    private string DescribeSignature(string signatureId, string name)
    {
        var matches = MatchSites(name);
        var suffix = matches.Count == 0 ? string.Empty : SdeSiteDescription.DescribeMatches(matches);
        return suffix.Length == 0 ? $"{signatureId} · {name}" : $"{signatureId} · {name} — {suffix}";
    }

    /// <summary>The one route from a copied site name into the catalogue — the toast and the window it opens must
    /// not answer differently. Exact match across every SDE locale (ET-79 AC-4); a miss does not prove the site is
    /// missing (Data Site, Relic Site and Wormhole are not in the catalogue at all) and no longer stops it becoming
    /// a run (ET-178) — it only means there is nothing further to add.</summary>
    private IReadOnlyList<SdeSite> MatchSites(string name) => _sde.FindSitesByExactName(name);

    /// <summary>A wormhole is a hole, not a site to run. On the SDE type id and not on the group column, because that
    /// column is whatever language the client runs in (ET-79 §4); the catalogue cannot answer it either, carrying no
    /// wormhole at all. Group 988 is nothing but wormholes and no site name in any of the eight locales resolves into
    /// it — both measured on build 3494416.</summary>
    private bool IsWormhole(string name) =>
        _sde.TryGetTypeId(name, out int typeId)
        && (WormholeTypeIdsOutsideTheWormholeGroup.Contains(typeId) || _sde.GetType(typeId)?.GroupId == WormholeGroupId);

    private const int WormholeGroupId = 988;

    // Unstable, Violent, Stable and Unidentified Wormhole — the four names the scan window writes for a hole nobody
    // has been through yet. CCP files them under group 226 (Large Collidable Object) rather than 988, and 226 also
    // holds real site names (Serpentis Fortress, Angel Hideout — 88 of the 1409 catalogue names are type names too),
    // so the group cannot stand in for them. A fifth generic appearance name has to be added here by hand.
    private static readonly int[] WormholeTypeIdsOutsideTheWormholeGroup = [26272, 32386, 32387, 34494];
}
