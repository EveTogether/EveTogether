using System;
using System.Security.Cryptography;
using System.Text;
using EveUtils.Client.Notifications;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Clipboard;

public sealed class AbyssalLootCapture : ISingletonService, IDisposable
{
    public const string FeatureName = "Abyssal run loot";

    private readonly IToastService _toasts;
    private readonly ISdeAccessor _sde;
    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;

    private string? _openFingerprint;

    public AbyssalLootCapture(ClipboardWatchService clipboardWatch, IToastService toasts, ISdeAccessor sde)
    {
        _toasts = toasts;
        _sde = sde;
        _subscription = clipboardWatch.Subscribe(FeatureName, OnCapture);
    }

    public void Dispose() => _subscription.Dispose();

    private void OnCapture(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Inventory)
            return;

        IReadOnlyList<ClipboardInventoryItem> items = ClipboardInventoryParser.Parse(capture.Text);
        var resolution = SdeInventoryResolver.Resolve(items, _sde);
        if (resolution.Lines.Count * 2 <= items.Count)
            return; // A strict majority prevents an incidental EVE name from turning a copied table into loot.

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Text)));
        lock (_gate)
        {
            if (_openFingerprint == fingerprint)
                return;

            _openFingerprint = fingerprint;
        }

        _toasts.Show("Loot copied", $"Recognised {resolution.Lines.Count} EVE item type(s) from this inventory.",
            ToastKind.Information, [new ToastAction("Close", () => CloseOffer(fingerprint))],
            () => CloseOffer(fingerprint), FeatureName);
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
