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

    // Wormholes are excluded for now; English-only site labels are a known limitation until a locale alias source exists.
    private static bool IsActivitySite(string? siteType) => siteType is "Combat Site" or "Homefront Operation Site - Combat Site";

    private void StartRun(string fingerprint, ClipboardSignatureRow row)
    {
        lock (_gate)
        {
            // Guards the same click landing twice before the card visually closes — without it, two clicks open two
            // windows (ET-100 AC-5).
            if (_startedRunFingerprint == fingerprint)
                return;

            _startedRunFingerprint = fingerprint;
        }

        _dialogs.ShowActivityWindow(new ActivityWindowViewModel(ActivityKind.Site, _services)
        {
            SignatureGroup = row.Group,
            SignatureName = row.Name
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
        // English-only exact match (ET-79 AC-4b: the multi-language alias table is an open decision). A miss does
        // not prove the site is missing from the catalogue, so the toast says that honestly instead of the
        // stronger, unearned "not in the site catalogue".
        var matches = _sde.SearchSites(nameQuery: name)
            .Where(site => string.Equals(site.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return $"{signatureId} · {name} — site names are matched in English only";

        var suffix = DescribeMatches(matches);
        return suffix.Length == 0 ? $"{signatureId} · {name}" : $"{signatureId} · {name} — {suffix}";
    }

    // Never fabricates a field the SDE does not carry and never picks one match over another (ET-79 AC-5): folds
    // matches that show the same thing into one, and for anything that still differs shows only what they share
    // plus how many variants there are.
    private static string DescribeMatches(IReadOnlyList<SdeSite> matches)
    {
        var distinctDescriptions = matches.Select(DescribeOne).Distinct().ToList();
        if (distinctDescriptions.Count == 1)
            return distinctDescriptions[0];

        var shared = DescribeShared(matches);
        var variants = $"{distinctDescriptions.Count} variants";
        return shared.Length == 0 ? variants : $"{shared} · {variants}";
    }

    private static string DescribeOne(SdeSite site)
    {
        var facts = new List<string>();
        if (site.ArchetypeName is not null)
            facts.Add(site.ArchetypeName);
        if (IsKnownFaction(site.FactionName))
            facts.Add(site.FactionName!);
        if (site.DedRating is { } ded)
            facts.Add($"DED {ded}");
        if (site.IsShipRestricted)
            facts.Add("ship-restricted");

        return string.Join(" · ", facts);
    }

    // Shares only what every remaining match agrees on; anything the matches disagree on is left out rather than
    // guessed at (ET-79 AC-5).
    private static string DescribeShared(IReadOnlyList<SdeSite> matches)
    {
        var facts = new List<string>();
        if (matches.Select(s => s.ArchetypeName).Distinct().ToList() is [{ } archetype])
            facts.Add(archetype);
        if (matches.Select(s => IsKnownFaction(s.FactionName) ? s.FactionName : null).Distinct().ToList() is [{ } faction])
            facts.Add(faction);
        if (matches.Select(s => s.DedRating).Distinct().ToList() is [{ } ded])
            facts.Add($"DED {ded}");
        if (matches.Select(s => s.IsShipRestricted).Distinct().ToList() is [true])
            facts.Add("ship-restricted");

        return string.Join(" · ", facts);
    }

    private static bool IsKnownFaction(string? factionName) => factionName is not (null or "Unknown");
}
