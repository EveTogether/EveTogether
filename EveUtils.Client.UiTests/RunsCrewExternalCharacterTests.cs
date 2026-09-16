using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-306: CREW named a crew member who is neither this machine's own <c>LocalCharacter</c> nor carries a run-start
/// <c>CharacterNameSnapshot</c> as a bare "character {id}", even though <c>CachedExternalCharacter</c> already knew
/// (or could learn) their name — exactly the abyssal HF-QTY3 case (RaymondKrah, 883434905) the ticket was filed from.
/// <see cref="RunsOverviewViewModel._NameOf"/> now goes through <see cref="RunsCharacterNames"/>, own characters
/// first and <see cref="IExternalCharacterLookup"/> (the same cache-then-ESI path the external-member flow already
/// uses) second, resolved once per activity read before a single row is built.
/// </summary>
public sealed class RunsCrewExternalCharacterTests
{
    private const long Own = 90000001;
    private const long External = 883434905;
    private const long StillUnknown = 990000001;
    private static readonly DateTime Evening = new(2026, 9, 11, 19, 34, 0, DateTimeKind.Utc);

    /// <summary>The ticket's own acceptance case: a crew member the external lookup can name reads that name, not
    /// the bare id — and their face's initial follows the real name too (AC-4), not the fallback text's own "C".</summary>
    [AvaloniaFact]
    public async Task Crew_ResolvesAnExternalCharacter_ThroughTheCache()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var lookup = new FakeExternalLookup { [(int)External] = "RaymondKrah" };
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IExternalCharacterLookup>(lookup));
        RunsOverviewViewModel viewModel = await _PresentedAsync(instance, cancellationToken);

        ActivityOverviewRowViewModel row = _Row(viewModel);
        viewModel.Select(row);
        await ActivityWindowHarness.WaitUntil(() => viewModel.Pane.Crew.Count == 2);

        ActivityRunRowViewModel externalCrew = viewModel.Pane.Crew.Single(crew => crew.CharacterText != "Jithran");
        Assert.Equal("RaymondKrah", externalCrew.CharacterText);
        Assert.Equal("R", externalCrew.Face.Initial);

        // Row's own crew line (ET-247) and the ET-162 detail screen share the same resolver, so the collapsed row
        // never disagrees with the pane it opens.
        Assert.Contains("RaymondKrah", row.CrewText);
    }

    /// <summary>Counter-proof: an id the lookup itself does not know (never added by ESI, never cached) still falls
    /// back to the bare id — the fallback is narrowed to "nothing is known anywhere", not removed.</summary>
    [AvaloniaFact]
    public async Task Crew_FallsBackToTheBareId_OnlyWhenNothingIsKnown()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var lookup = new FakeExternalLookup();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IExternalCharacterLookup>(lookup));
        RunsOverviewViewModel viewModel = await _PresentedAsync(instance, cancellationToken, StillUnknown);

        ActivityOverviewRowViewModel row = _Row(viewModel);
        viewModel.Select(row);
        await ActivityWindowHarness.WaitUntil(() => viewModel.Pane.Crew.Count == 2);

        ActivityRunRowViewModel unknownCrew = viewModel.Pane.Crew.Single(crew => crew.CharacterText != "Jithran");
        Assert.Equal($"character {StillUnknown}", unknownCrew.CharacterText);
    }

    private static ActivityOverviewRowViewModel _Row(RunsOverviewViewModel viewModel) =>
        Assert.Single(viewModel.Tabs[0].Days.SelectMany(day => day.Rows));

    private static async Task<RunsOverviewViewModel> _PresentedAsync(
        TestClientInstance instance, CancellationToken cancellationToken, long otherCharacterId = External)
    {
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await _SaveRunAsync(dispatcher, Own, cancellationToken);
        await _SaveRunAsync(dispatcher, otherCharacterId, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            [new Character("Jithran", (int)Own)], runClock: false, paneReadDelay: TimeSpan.Zero);
        await viewModel.LoadAsync(cancellationToken);
        return viewModel;
    }

    /// <summary>Two runs under the same group code, neither carrying a <c>CharacterNameSnapshot</c> — the exact shape
    /// of a fleet mate's run pulled in by server sync from an older client (ET-212's own fallback case).</summary>
    private static async Task _SaveRunAsync(ICqrsDispatcher dispatcher, long characterId, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, Evening,
            1234, "Homefront", 30000142, "HF-QTY3"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, Evening.AddMinutes(15), Evening.AddMinutes(16),
            [], [], [], []), cancellationToken);
    }
}
