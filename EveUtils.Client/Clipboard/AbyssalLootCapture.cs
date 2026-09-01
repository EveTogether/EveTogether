using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Clipboard;

public sealed class AbyssalLootCapture : ISingletonService, IDisposable
{
    public const string FeatureName = "Abyssal run loot";

    private readonly IToastService _toasts;
    private readonly ISdeAccessor _sde;
    private readonly IDispatcher _dispatcher;
    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;

    private string? _openFingerprint;

    public AbyssalLootCapture(ClipboardWatchService clipboardWatch, IToastService toasts, ISdeAccessor sde,
        IDispatcher dispatcher)
    {
        _toasts = toasts;
        _sde = sde;
        _dispatcher = dispatcher;
        _subscription = clipboardWatch.Subscribe(FeatureName, OnCapture);
    }

    /// <summary>The write the clipboard callback cannot wait for, so a test can.</summary>
    internal Task LastStore { get; private set; } = Task.CompletedTask;

    public void Dispose() => _subscription.Dispose();

    private void OnCapture(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Inventory)
            return;

        IReadOnlyList<ClipboardInventoryItem> items = ClipboardInventoryParser.Parse(capture.Text);
        var resolution = SdeInventoryResolver.Resolve(items, _sde);
        if (resolution.Lines.Count == 0)
        {
            _toasts.Show("Loot not recognised",
                $"None of the {resolution.Unresolved.Count} copied names is a known item type. Copy rows from an EVE inventory window.",
                ToastKind.Error);
            return;
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Text)));
        lock (_gate)
        {
            // Suppress only while its card stays open, so copying after dismissal asks again.
            if (_openFingerprint == fingerprint)
                return;

            _openFingerprint = fingerprint;
        }

        LastStore = StoreAsync(fingerprint, resolution.Lines);

        string message = $"Recognised {resolution.Lines.Count} EVE item type(s) from this inventory."
            + (resolution.Unresolved.Count > 0 ? $" {resolution.Unresolved.Count} name(s) were not recognised." : string.Empty);
        _toasts.Show("Loot copied", message,
            ToastKind.Information, [new ToastAction("Close", () => CloseOffer(fingerprint))],
            () => CloseOffer(fingerprint), FeatureName);
    }

    private Task StoreAsync(string fingerprint, IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> lines) =>
        _dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
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

    private void CloseOffer(string fingerprint)
    {
        lock (_gate)
        {
            if (_openFingerprint == fingerprint)
                _openFingerprint = null;
        }
    }
}
