using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Clipboard;

/// <summary>Loot copied out of an EVE inventory window, onto whichever run is going (ET-65). Nothing here asks what
/// kind of run that is — it was only ever named after the abyss because that was the only kind at the time.</summary>
public sealed class ClipboardLootCapture : ISingletonService, IDisposable
{
    public const string FeatureName = "Run loot";

    private readonly IToastService _toasts;
    private readonly ISdeAccessor _sde;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<ClipboardLootCapture> _logger;
    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;

    private string? _openFingerprint;

    public ClipboardLootCapture(ClipboardWatchService clipboardWatch, IToastService toasts, ISdeAccessor sde,
        ILogger<ClipboardLootCapture> logger,
        IDispatcher dispatcher)
    {
        _toasts = toasts;
        _sde = sde;
        _dispatcher = dispatcher;
        _logger = logger;
        _subscription = clipboardWatch.Subscribe(FeatureName, OnCapture);
    }

    /// <summary>The write the clipboard callback cannot wait for, so a test can — and so a dispatcher exception
    /// (e.g. the database is locked) surfaces as a toast instead of vanishing off a fire-and-forget Task.
    /// A second capture or a toggle click can start before the first store settles, so every assignment chains
    /// onto whatever was still pending rather than replacing it — otherwise awaiting this later would miss the
    /// earlier one's completion (and, if it ever threw, its exception).</summary>
    internal Task LastStore { get; private set; } = Task.CompletedTask;

    /// <summary>Folds <paramref name="current"/> into <see cref="LastStore"/> alongside whatever was already
    /// pending, instead of discarding it.</summary>
    private void _TrackLastStore(Task current) => LastStore = Task.WhenAll(LastStore, current);

    public void Dispose() => _subscription.Dispose();

    /// <summary>
    /// The one place that says why a copy did not become loot. Every path in <see cref="OnCapture"/> that ends in
    /// doing nothing comes through here, so "the watch fired and the content was refused" can be told apart from
    /// "nothing reached this feature at all" — which was the whole difficulty in ET-65: most of those paths were
    /// silent on screen and silent in the log, and looked exactly like a watcher that never ran.
    ///
    /// <c>Warning</c> deliberately: <c>AppLogger</c> drops anything below it, so an Information line would be
    /// invisible precisely when it is wanted. It only ever runs on a payload the watch already recognised as one
    /// of the three EVE shapes, so it is not a line per copy of the day.
    ///
    /// <paramref name="reason"/> is derived from the payload, never taken from it: ET-57 promises that clipboard
    /// text is never written down, and that holds for this log too.
    /// </summary>
    private void _Dropped(string reason) =>
        _logger.LogWarning("Clipboard copy not recorded as run loot: {Reason}.", reason);

    private void OnCapture(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Inventory)
        {
            _Dropped($"the clipboard held a {capture.Shape} payload, not an inventory listing");
            return;
        }

        InventoryTextReading reading = InventoryTextReading.Read(capture.Text, _sde);
        if (reading.Lines.Count == 0)
        {
            // One copied line that matches no item type is far more often an ordinary copy than lost loot, so that
            // one case stays quiet on screen — but it says so in the log like every other refusal.
            if (reading.IsSingleUnknownRow)
            {
                _Dropped("a single copied row that matches no known item type");
                return;
            }

            if (reading.Refusal is { } refusal)
            {
                _toasts.Show("Loot not recognised", refusal, ToastKind.Error);
                _Dropped(refusal);
            }

            return;
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Text)));
        lock (_gate)
        {
            // Suppress only while its card stays open, so copying after dismissal — or a failed save — asks again.
            if (_openFingerprint == fingerprint)
            {
                _Dropped("the same copy is already on screen as an open card");
                return;
            }

            _openFingerprint = fingerprint;
        }

        _TrackLastStore(StoreAndOfferAsync(fingerprint, reading.Lines, reading.UnresolvedCount));
    }

    /// <summary>Stores the capture and only then tells the player what actually happened — recorded, refused with a
    /// reason, or kept as an excluded repeat — instead of announcing "recognised" before the save is known to have
    /// worked (ET-65 AC-5/AC-7 review finding).</summary>
    private async Task StoreAndOfferAsync(string fingerprint,
        IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> lines, int unresolvedCount)
    {
        Result<RunLootCaptureSaveResult> result;
        try
        {
            result = await _dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
            {
                CapturedAtUtc = DateTime.UtcNow,
                Source = LootCaptureSource.Clipboard,
                ContentHash = fingerprint,
                Entries =
                [
                    .. lines.Select(resolved => new RunLootEntryInput
                    {
                        ItemTypeId = resolved.Line.TypeId,
                        Name = resolved.Line.Name,
                        Quantity = resolved.Line.Quantity,
                        // The clipboard columns as they stood in the window, not a valuation.
                        Volume = resolved.Item.Volume,
                        ClipboardPrice = resolved.Item.Price,
                        LootKind = LootKind.Gained
                    })
                ]
            }));
        }
        catch (Exception ex)
        {
            CloseOffer(fingerprint);
            _toasts.Show("Loot not recorded", $"Saving this copy failed: {ex.Message}", ToastKind.Error);
            return;
        }

        if (!result.IsSuccess)
        {
            CloseOffer(fingerprint);
            var reason = result.Messages.Count > 0 ? result.Messages[0].Text : "This copy was not recorded.";
            _toasts.Show("Loot not recorded", reason, ToastKind.Error);
            return;
        }

        RunLootCaptureSaveResult saved = result.Value!;
        var unresolvedSuffix = unresolvedCount > 0 ? $" {unresolvedCount} name(s) were not recognised." : string.Empty;

        if (saved.RepeatOfCapturedAtUtc is { } repeatOf)
        {
            _toasts.Show("Loot copy repeated",
                $"Identical to the copy at {repeatOf:HH:mm:ss} — kept, but excluded from the run's total.{unresolvedSuffix}",
                ToastKind.Information,
                [new ToastAction("Include", () => SetExcluded(saved.CaptureId, isExcluded: false)),
                    new ToastAction("Close", () => CloseOffer(fingerprint))],
                () => CloseOffer(fingerprint), FeatureName);
            return;
        }

        _toasts.Show("Loot copied", $"Recognised {lines.Count} EVE item type(s) from this inventory.{unresolvedSuffix}",
            ToastKind.Information,
            [new ToastAction("Exclude", () => SetExcluded(saved.CaptureId, isExcluded: true)),
                new ToastAction("Close", () => CloseOffer(fingerprint))],
            () => CloseOffer(fingerprint), FeatureName);
    }

    /// <summary>The toast's own one-click exclude/include — the same flag <see cref="EveUtils.Client.ViewModels.Runs.RunLootViewModel"/>
    /// toggles later, so a card acted on now and a still-open list agree.</summary>
    private void SetExcluded(Guid captureId, bool isExcluded) => _TrackLastStore(SetExcludedAsync(captureId, isExcluded));

    /// <summary>Same treatment as <see cref="StoreAndOfferAsync"/>: a dispatcher exception or a clean refusal is
    /// caught here and shown, not left to vanish off the button click that triggered it.</summary>
    private async Task SetExcludedAsync(Guid captureId, bool isExcluded)
    {
        Result result;
        try
        {
            result = await _dispatcher.Send(new SetRunLootCaptureExclusionCommand(captureId, isExcluded));
        }
        catch (Exception ex)
        {
            _toasts.Show("Loot not updated", $"Could not {(isExcluded ? "exclude" : "include")} this capture: {ex.Message}",
                ToastKind.Error);
            return;
        }

        if (!result.IsSuccess)
        {
            var reason = result.Messages.Count > 0 ? result.Messages[0].Text : "This capture could not be updated.";
            _toasts.Show("Loot not updated", reason, ToastKind.Error);
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
}
