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
/// was on the run" section bound to it (<c>ActivityWindow.axaml:762,779</c>) hid itself forever. The proof drives the
/// window the way the app does — <c>StartRunCommand</c>, then a tick — never a hand-built list.
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

}
