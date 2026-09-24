using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Dispatcher = Avalonia.Threading.Dispatcher;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-382: a fit stored into the local library is announced, so a library list that is already open shows it however
/// it got there (a clipboard offer or another window, not only the screen's own import button).
/// </summary>
public sealed class FittingsChangedSignalTests
{
    private static readonly EsiFitting Guardian = new(1, "Guardian", "", 11987, []);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TextImport_StoringAFit_PublishesOneFittingsChanged()
    {
        using TestClientInstance instance = _Create();
        List<FittingsChangedEvent> heard = _Listen(instance);

        Result<string> result = await instance.Services.GetRequiredService<IDispatcher>()
            .Send(new ImportFitFromTextCommand("[Guardian, Guardian]"), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, Assert.Single(heard).Data.Count);
    }

    [Fact]
    public async Task TextImport_OfAFitAlreadyInTheLibrary_PublishesNothing()
    {
        using TestClientInstance instance = _Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new ImportFitFromTextCommand("[Guardian, Guardian]"), Ct);
        List<FittingsChangedEvent> heard = _Listen(instance);

        Result<string> again = await dispatcher.Send(new ImportFitFromTextCommand("[Guardian, Guardian]"), Ct);

        Assert.True(again.IsSuccess);
        Assert.Empty(heard);
    }

    [Fact]
    public async Task EsiImport_OfFitsAlreadyInTheLibrary_PublishesNothing()
    {
        using TestClientInstance instance = _Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new ImportFittingsFromEsiCommand(90000001, [Guardian]), Ct);
        List<FittingsChangedEvent> heard = _Listen(instance);

        Result<int> again = await dispatcher.Send(new ImportFittingsFromEsiCommand(90000001, [Guardian]), Ct);

        Assert.Equal(0, again.Value);
        Assert.Empty(heard);
    }

    /// <summary>Red if the library list only reloads after its own import: a fit stored by anything else (the
    /// clipboard offer, another window) stays invisible until the pilot reopens the tab.</summary>
    [AvaloniaFact]
    public async Task OpenLibraryList_ShowsAFitStoredByAnotherPath_WithoutItsOwnImport()
    {
        using TestClientInstance instance = TestClientInstance.Create(
            services => services.AddSingleton<IDialogService, RecordingDialogService>());
        var shell = new MainWindowViewModel(instance.Services);
        Assert.Empty(shell.Fittings);

        await instance.Services.GetRequiredService<IDispatcher>()
            .Send(new ImportFittingsFromEsiCommand(90000001, [Guardian]), Ct);

        Assert.True(await _WaitForAsync(() => shell.Fittings.Count == 1));
    }

    private static TestClientInstance _Create() => TestClientInstance.Create(services =>
        services.AddSingleton<IFitTextImporter>(new StubFitTextImporter(FitImportResult.Ok(Guardian, []))));

    private static List<FittingsChangedEvent> _Listen(TestClientInstance instance)
    {
        List<FittingsChangedEvent> heard = [];
        instance.Services.GetRequiredService<IEventBus>().Subscribe<FittingsChangedEvent>(published => heard.Add(published));
        return heard;
    }

    private static async Task<bool> _WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return true;

            await Task.Delay(20, Ct);
        }

        return condition();
    }

    private sealed class StubFitTextImporter(FitImportResult result) : IFitTextImporter
    {
        public FitTextFormat Detect(string text) => default;

        public FitImportResult Import(string text) => result;
    }
}
