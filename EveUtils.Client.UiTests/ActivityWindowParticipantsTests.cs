using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-131: <c>ActivityWindowViewModel.Participants</c> was declared, read and cleared but never filled — the "who
/// was on the run" section bound to it (<c>ActivityWindow.axaml:762,779</c>) hid itself forever, and
/// <c>RunPayoutSplit.Apply</c> (run at every <c>TotalLootIsk</c> change) divided over a list that was always empty.
/// Both proofs drive the window the way the app does — <c>StartRunCommand</c>, then a tick — never a hand-built
/// list, which would measure the split rule instead of the fill.
/// </summary>
public sealed class ActivityWindowParticipantsTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
    private const int CharacterA = 90000031;
    private const int CharacterB = 90000032;
    private const string GroupCode = "HF-P1G2";

    /// <summary>The gap itself: two characters share a group code, and the window opened on one of those runs must
    /// show both — not the empty collection <c>Participants</c> was left at everywhere but its declaration.</summary>
    [AvaloniaFact]
    public async Task ARunSharingAGroupCode_ShowsBothParticipants()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> runA = await dispatcher.Send(new StartRunCommand(CharacterA, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, GroupCode: GroupCode), cancellationToken);
        await dispatcher.Send(new StartRunCommand(CharacterB, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, GroupCode: GroupCode), cancellationToken);

        using var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services)
        {
            RunId = runA.Value,
            GroupCode = GroupCode
        };

        for (var attempt = 0; attempt < 100 && window.Participants.Count < 2; attempt++)
        {
            window.Refresh(DateTime.UtcNow);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, cancellationToken);
        }

        Assert.Equal(2, window.Participants.Count);
        Assert.Contains(window.Participants, p => p.CharacterId == CharacterA);
        Assert.Contains(window.Participants, p => p.CharacterId == CharacterB);
    }

    /// <summary>Raymond, 2026-09-05: "controleer dat wat [RunPayoutSplit.Apply] dan doet klopt, in plaats van aan te
    /// nemen dat die code goed was omdat hij nooit is aangeroepen." Once the fill above lands two eligible
    /// participants in <c>Participants</c>, setting <c>TotalLootIsk</c> must divide it evenly over both — through
    /// the window's own <c>RecomputePayout</c>, not a hand-built list.</summary>
    [AvaloniaFact]
    public async Task OnceParticipantsAreFilled_ThePayoutSplitDividesTheTotalOverThem()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> runA = await dispatcher.Send(new StartRunCommand(CharacterA, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, GroupCode: GroupCode), cancellationToken);
        await dispatcher.Send(new StartRunCommand(CharacterB, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, GroupCode: GroupCode), cancellationToken);

        using var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services)
        {
            RunId = runA.Value,
            GroupCode = GroupCode
        };

        for (var attempt = 0; attempt < 100 && window.Participants.Count < 2; attempt++)
        {
            window.Refresh(DateTime.UtcNow);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, cancellationToken);
        }

        window.TotalLootIsk = 400m;

        Assert.Equal(2, window.Participants.Count);
        Assert.All(window.Participants, p => Assert.Equal(200m, p.PayoutIsk));
    }
}
