using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.Clipboard;

/// <summary>Shows what the SDE knows about a copied cosmic signature or anomaly, without starting anything (ET-79).</summary>
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
    private string? _startedRunFingerprint;

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
        var actions = new List<ToastAction> { new("Close", () => CloseOffer(fingerprint)) };

        // ET-100 testopener: ET-98's window (merged same day) has no way in otherwise. Not the abyssal opener (that
        // needs a filament, not a signature) and not final — a later opener may replace or keep this. Needs exactly
        // one fully-scanned row: half-scanned opens empty, 2+ rows is a menu a toast is the wrong surface for.
        var recognised = rows.Where(row => IsActivitySite(row.Group) && row.Name is not null).ToList();
        if (recognised is [{ } row])
            actions.Add(new ToastAction("Start run", () => StartRun(fingerprint, row), ToastActionStyle.Affirmative));

        _toasts.Show("Signature copied", BuildMessage(rows), ToastKind.Information, actions,
            () => CloseOffer(fingerprint), FeatureName);
    }

    // Wormholes are excluded for now. This still matches the group text literally in English — SiteNameAlias (ET-79)
    // covers site names, not the scan-window group column.
    private static bool IsActivitySite(string? siteType) => siteType?.EndsWith("Combat Site", StringComparison.Ordinal) is true;

    private void StartRun(string fingerprint, ClipboardSignatureRow row)
    {
        lock (_gate)
        {
            // Guards the same click landing twice before the card visually closes — without it, two clicks open two
            // windows (ET-100 AC-5). Left set until the card's own close does its usual cleanup, so this alone does
            // not defeat the guard above.
            if (_startedRunFingerprint == fingerprint)
                return;

            _startedRunFingerprint = fingerprint;

            // The offer itself is answered now, so a fresh copy of the same signature must ask again rather than be
            // swallowed by a guard the card's own (possibly much later) close would otherwise have to clear.
            if (_openFingerprint == fingerprint)
                _openFingerprint = null;
        }

        _dialogs.ShowActivityWindow(new ActivityWindowViewModel(ActivityKind.Site, _services)
        {
            // The scan id travels with the site: two Sansha Refuges scanned an hour apart are two runs, and only
            // this tells them apart.
            SignatureId = row.SignatureId,
            SignatureGroup = row.Group,
            SignatureName = row.Name,
            MatchedSites = MatchSites(row.Name!)
        });
    }

    private void CloseOffer(string fingerprint)
    {
        lock (_gate)
        {
            if (_openFingerprint == fingerprint)
                _openFingerprint = null;
            if (_startedRunFingerprint == fingerprint)
                _startedRunFingerprint = null;
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
