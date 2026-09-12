using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-254 AC-1/AC-6: the startup notice offering back a run the previous process left running. Staged before the
/// main window exists (<c>Program.cs</c>, before <c>StartWithClassicDesktopLifetime</c> — there is nowhere for a
/// toast to attach to yet), shown once something asks — <c>MainWindow.OnOpened</c> in the real app, this test
/// directly.
/// </summary>
public sealed class StartupResumeNoticeServiceTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly DateTime StoppedAtUtc = new(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc);

    private static StoppedRunDto _StoppedRun() =>
        new(RunId, 90000001, ActivityKind.Site, "Sansha Hideaway", "Combat Site", StoppedAtUtc);

    [AvaloniaFact]
    public void Staged_ShowsOneToastNamingTheRun_WithResumeAndKeepStopped()
    {
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IToastService>(toasts));
        var service = instance.Services.GetRequiredService<StartupResumeNoticeService>();
        service.Stage([_StoppedRun()]);

        service.ShowPending();

        var toast = Assert.Single(toasts.ActionToasts);
        Assert.Equal("EVE Together closed while Sansha Hideaway was running", toast.Title);
        Assert.Equal(2, toast.Actions.Count);
        Assert.Equal("Resume", toast.Actions[0].Label);
        Assert.Equal("Keep stopped", toast.Actions[1].Label);
    }

    /// <summary>Counter-proof for "shown once something asks": <c>Stage</c> alone shows nothing, and nothing staged
    /// shows nothing either — <c>ShowPending</c> is the only trigger, same as <c>StartSdeUpdateCheck</c>'s own
    /// Opened-driven shape.</summary>
    [AvaloniaFact]
    public void NothingStaged_ShowsNoToast()
    {
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IToastService>(toasts));

        instance.Services.GetRequiredService<StartupResumeNoticeService>().ShowPending();

        Assert.Empty(toasts.ActionToasts);
    }

    [AvaloniaFact]
    public async Task ClickingResume_OpensTheWindow_NamedOnTheRunsOwnPilot()
    {
        var toasts = new RecordingToastService();
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IDialogService>(dialogs);
        });
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Ra Vinter", 90000001));
        var service = instance.Services.GetRequiredService<StartupResumeNoticeService>();
        service.Stage([_StoppedRun()]);
        service.ShowPending();
        var toast = Assert.Single(toasts.ActionToasts);

        toast.Actions[0].Run(); // Resume — fire-and-forget from the toast's own synchronous callback
        await Task.Delay(50, TestContext.Current.CancellationToken);

        ActivityWindowViewModel opened = Assert.Single(dialogs.ShownActivityWindows);
        Assert.Equal(ActivityKind.Site, opened.Kind);
        Assert.Equal((90000001, "Ra Vinter"), opened.PickedCharacter);
    }

    [AvaloniaFact]
    public void ClickingKeepStopped_OpensNoWindow()
    {
        var toasts = new RecordingToastService();
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IDialogService>(dialogs);
        });
        var service = instance.Services.GetRequiredService<StartupResumeNoticeService>();
        service.Stage([_StoppedRun()]);
        service.ShowPending();
        var toast = Assert.Single(toasts.ActionToasts);

        toast.Actions[1].Run(); // Keep stopped

        Assert.Empty(dialogs.ShownActivityWindows);
    }
}
