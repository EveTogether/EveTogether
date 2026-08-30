using System;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Fittings;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class ClipboardFitImportOfferTests
{
    [AvaloniaFact]
    public async Task FitCapture_OffersImportOnceAndCanBeIgnoredForToday()
    {
        var source = new FakeClipboardChangeSource();
        var dialogs = new RecordingDialogService();
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create();
        using var watch = new ClipboardWatchService(dialogs, instance.Services,
            NullLogger<ClipboardWatchService>.Instance, source);
        using var offers = new ClipboardFitImportOffer(watch, toasts, instance.Services);
        await watch.SetEnabledAsync(true);

        dialogs.ClipboardText = "[Jackdaw, Jackdaw - T1/T2 - D]\r\nBallistic Control System II\r\nBallistic Control System II";
        Copy(source);
        Copy(source);

        var firstOffer = Assert.Single(toasts.ActionToasts);
        Assert.Equal("Fit copied", firstOffer.Title);
        Assert.Equal(new[] { "Ignore this fit", "Not today", "Import" },
            Array.ConvertAll(firstOffer.Actions.ToArray(), action => action.Label));

        firstOffer.Actions[1].Run();
        dialogs.ClipboardText = "[Armageddon, PVE High DPS 0-60KM (1000+)]\r\nBallistic Control System II";
        Copy(source);

        Assert.Single(toasts.ActionToasts);
    }

    [AvaloniaFact]
    public async Task InventoryCapture_DoesNotOfferFitImport()
    {
        var source = new FakeClipboardChangeSource();
        var dialogs = new RecordingDialogService();
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create();
        using var watch = new ClipboardWatchService(dialogs, instance.Services,
            NullLogger<ClipboardWatchService>.Instance, source);
        using var offers = new ClipboardFitImportOffer(watch, toasts, instance.Services);
        await watch.SetEnabledAsync(true);

        dialogs.ClipboardText = "Agitated Exotic Filament\t1\tAbyssal Filaments\t\t\t0,10 m3\t42.237,65 ISK\r\n" +
                                "Baryon Exotic Plasma S Blueprint\t\tExotic Plasma Charge Blueprint\t\t\t0,01 m3\t";
        Copy(source);

        Assert.Empty(toasts.ActionToasts);
    }

    [AvaloniaFact]
    public async Task NewFitCapture_ReplacesTheOfferWithTheMostRecentlyCopiedFit()
    {
        var source = new FakeClipboardChangeSource();
        var dialogs = new RecordingDialogService();
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create();
        using var watch = new ClipboardWatchService(dialogs, instance.Services,
            NullLogger<ClipboardWatchService>.Instance, source);
        using var offers = new ClipboardFitImportOffer(watch, toasts, instance.Services);
        await watch.SetEnabledAsync(true);

        dialogs.ClipboardText = "[Jackdaw, Jackdaw - T1/T2 - D]\r\nBallistic Control System II";
        Copy(source);
        dialogs.ClipboardText = "[Armageddon, PVE High DPS 0-60KM (1000+)]\r\nBallistic Control System II";
        Copy(source);

        var latestOffer = toasts.ActionToasts[^1];
        Assert.Contains("[Armageddon, PVE High DPS 0-60KM (1000+)]", latestOffer.Message);
    }

    [AvaloniaFact]
    public async Task ImportAction_UsesTheExistingFitTextImporter()
    {
        const string text = "[Jackdaw, Jackdaw - T1/T2 - D]\r\nBallistic Control System II\r\nBallistic Control System II";
        var importer = new RecordingFitTextImporter(FitImportResult.Ok(
            new EsiFitting(0, "Jackdaw - clipboard", "", 23533, []), []));
        var source = new FakeClipboardChangeSource();
        var dialogs = new RecordingDialogService();
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IFitTextImporter>(importer));
        using var watch = new ClipboardWatchService(dialogs, instance.Services,
            NullLogger<ClipboardWatchService>.Instance, source);
        using var offers = new ClipboardFitImportOffer(watch, toasts, instance.Services);
        await watch.SetEnabledAsync(true);

        dialogs.ClipboardText = text;
        Copy(source);
        toasts.ActionToasts[0].Actions[2].Run();

        var repository = instance.Services.GetRequiredService<IFittingRepository>();
        for (var attempt = 0; attempt < 100 && (await repository.ListAllAsync()).Count == 0; attempt++)
            await Task.Delay(10);

        Assert.Equal(text, importer.ImportedText);
        Assert.Equal("Jackdaw - clipboard", Assert.Single(await repository.ListAllAsync()).Name);
    }

    private static void Copy(FakeClipboardChangeSource source)
    {
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeClipboardChangeSource : IClipboardChangeSource
    {
        public bool IsSupported => true;

        public event Action? Changed;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public void RaiseChanged() => Changed?.Invoke();
    }

    private sealed class RecordingFitTextImporter(FitImportResult importResult) : IFitTextImporter
    {
        public string? ImportedText { get; private set; }

        public FitTextFormat Detect(string text) => default;

        public FitImportResult Import(string text)
        {
            ImportedText = text;
            return importResult;
        }
    }
}
