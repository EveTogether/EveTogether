using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>
/// The startup half of ET-254: a run <see cref="Shared.Modules.Runs.Commands.StopRunsLeftRunningCommand"/> just
/// stopped — the previous process quit or crashed with it still going — gets offered back the moment there is
/// somewhere on screen to offer it. <c>Program.cs</c> sends that command before the main window exists (deliberately:
/// a window must never adopt a run still wrongly <c>Running</c>), so this only stages what it stopped and shows the
/// offer later, once <c>MainWindow.OnOpened</c> asks.
///
/// A toast, not a dialog: it never takes the keyboard from a pilot who is looking at EVE, not this app, the moment
/// it appears (<see cref="IToastService"/> draws inside this app's own window and never calls anything like
/// <c>SetForegroundWindow</c> — the same reasoning <c>FleetRunWindowPresenter</c>'s own offer toast already rests
/// on). Resuming is the pilot's own click on a card already in front of them, so the window that opens for it takes
/// focus normally, same as any other button they press inside the app.
/// </summary>
public sealed class StartupResumeNoticeService(IServiceProvider services) : ISingletonService
{
    private IReadOnlyList<StoppedRunDto> _pending = [];

    /// <summary>Remembers what the startup sweep just stopped — called from <c>Program.cs</c>, before the main
    /// window exists.</summary>
    public void Stage(IReadOnlyList<StoppedRunDto> runs) => _pending = runs;

    /// <summary>Shows one offer per staged run, then forgets them — called once, from the main window's own Opened
    /// handler. A second call (there is only one main window, but nothing stops a caller from asking twice) finds
    /// nothing staged and does nothing.</summary>
    public void ShowPending()
    {
        IReadOnlyList<StoppedRunDto> runs = _pending;
        _pending = [];
        if (runs.Count == 0 || services.GetService<IToastService>() is not { } toasts)
            return;

        // A freerun is not asked about (ET-508): it was meant to go on, so it picks itself back up. Only the latest:
        // there is one run window, and a second would take it over — any other is offered like every other run.
        StoppedRunDto? resumed = runs
            .Where(run => RunTypeCatalogue.For(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId).SurvivesRestart)
            .MaxBy(run => run.StoppedAtUtc);
        if (resumed is not null)
        {
            ResumedRun = _ResumeAsync(resumed, RunWindowOpenTrigger.ResumedAtStartup);
            toasts.Show($"{OpsecText.Mark(resumed.SiteName)} picked back up",
                "EVE Together closed while it was running. It goes on from its original start, and what it collected "
                + "meanwhile is read back from the game log.", ToastKind.Information);
        }

        foreach (StoppedRunDto run in runs.Where(run => !ReferenceEquals(run, resumed)))
            _Offer(toasts, run);
    }

    /// <summary>The freerun picked back up by <see cref="ShowPending"/>, so a test can await it.</summary>
    internal Task ResumedRun { get; private set; } = Task.CompletedTask;

    private void _Offer(IToastService toasts, StoppedRunDto run)
    {
        // ET-275's archetype fallback needs a SiteName to look up, which this branch by definition does not have
        // (the branch above already reads it when there is one) — sde is still threaded through for the same reason
        // every other reader of RunTypeCatalogue.For now does, even though it can never fire here.
        string what = !string.IsNullOrWhiteSpace(run.SiteName)
            ? OpsecText.Mark(run.SiteName)
            : RunTypeCatalogue.For(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId,
                services.GetService<ISdeAccessor>()).Name;
        toasts.Show($"EVE Together closed while {what} was running",
            $"Stopped {run.StoppedAtUtc.ToLocalTime():d MMM HH:mm}, when this app last saw it going. "
            + "Resume picks the clock back up from its original start, with everything it already collected.",
            ToastKind.Information,
            [
                new ToastAction("Resume", () => _ = _ResumeAsync(run, RunWindowOpenTrigger.LocalUser), ToastActionStyle.Affirmative),
                new ToastAction("Keep stopped", () => { })
            ],
            onClosed: null, replacementKey: $"startup-resume:{run.RunId}");
    }

    private async Task _ResumeAsync(StoppedRunDto run, RunWindowOpenTrigger trigger)
    {
        if (services.GetService<IDialogService>() is not { } dialogs)
            return;

        ActivityWindowViewModel window = new(run.ActivityKind, services);
        // Named before the window loads (ET-221, the same reason every other opener that already knows its pilot
        // does this) — best-effort: a character this client no longer has registered still resumes, just asked
        // for again by ActivityWindowViewModel's own _ResolveCharacterAsync.
        if (services.GetService<ICharacterRegistry>() is { } registry
            && (await registry.GetAllAsync()).FirstOrDefault(character => character.EsiCharacterId == run.CharacterId)
                is { EsiCharacterId: { } characterId } character)
            window.UseCharacter(characterId, character.Name);
        window.ResumeRun(run.RunId);
        dialogs.ShowActivityWindow(window, trigger);
    }
}
