using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Fittings;

/// <summary>Offers a recognised EVE fit for import without interrupting the player's current activity.</summary>
public sealed class ClipboardFitImportOffer : ISingletonService, IDisposable
{
    public const string FeatureName = "Fit import offers";

    private readonly IToastService _toasts;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _offeredCaptures = new(StringComparer.Ordinal);
    private readonly IDisposable _subscription;

    private string? _openFingerprint;
    private string? _pendingText;
    private DateOnly? _mutedOn;

    public ClipboardFitImportOffer(ClipboardWatchService clipboardWatch, IToastService toasts, IServiceProvider services,
        TimeProvider? clock = null)
    {
        _toasts = toasts;
        _services = services;
        _clock = clock ?? TimeProvider.System;
        _subscription = clipboardWatch.Subscribe(FeatureName, OnCapture);
    }

    public void Dispose() => _subscription.Dispose();

    private void OnCapture(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Fit)
            return;

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Text)));
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
            if (_mutedOn == today || !_offeredCaptures.Add(fingerprint))
                return;

            _openFingerprint = fingerprint;
            _pendingText = capture.Text;
        }

        _toasts.Show("Fit copied", $"Import {FitHeader(capture.Text)} into your Local library?", ToastKind.Information,
        [
            new ToastAction("Ignore this fit", () => CloseOffer(fingerprint)),
            new ToastAction("Not today", () => MuteForToday(fingerprint)),
            new ToastAction("Import", () => _ = ImportAsync(fingerprint), ToastActionStyle.Affirmative),
        ], () => CloseOffer(fingerprint), FeatureName);
    }

    private async Task ImportAsync(string fingerprint)
    {
        string? text;
        lock (_gate)
        {
            if (_openFingerprint != fingerprint)
                return;

            text = _pendingText;
        }

        if (text is null)
            return;

        using var scope = _services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new ImportFitFromTextCommand(text));

        if (result.IsSuccess)
            _toasts.Show("Fit imported", $"'{result.Value}' is in your Local library.");
        else
            _toasts.Show("Fit import failed", result.Messages.FirstOrDefault()?.Text, ToastKind.Error);
    }

    private void MuteForToday(string fingerprint)
    {
        lock (_gate)
        {
            if (_openFingerprint != fingerprint)
                return;
            _mutedOn = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        }
        CloseOffer(fingerprint);
    }

    private void CloseOffer(string fingerprint)
    {
        lock (_gate)
        {
            if (_openFingerprint != fingerprint)
                return;

            _openFingerprint = null;
            _pendingText = null;
        }
    }

    private static string FitHeader(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var header = line.Trim();
            if (header.Length > 0)
                return header;
        }

        return "this fit";
    }
}
