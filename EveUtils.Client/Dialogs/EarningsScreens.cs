using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Earnings;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Dialogs;

/// <summary>Opens the screens the earnings panel leads to (ET-521). The runs overview is built here like the main window
/// builds it; the settings window is the main window's own, so it hands over how to open it once it is up.</summary>
public sealed class EarningsScreens(IServiceProvider services) : IEarningsScreens, ISingletonService
{
    /// <summary>Set by the main window: opens Settings on the category with that index.</summary>
    public Func<int, Task>? SettingsOpener { get; set; }

    public async Task OpenStatsAsync(ActivityStatsKey key)
    {
        if (services.GetService<IDialogService>() is not { } dialogs)
            return;

        IReadOnlyList<Character> characters = await services.GetRequiredService<ICharacterRegistry>().GetAllAsync();
        RunsOverviewViewModel shown = dialogs.ShowRuns(new RunsOverviewViewModel(
            services.GetRequiredService<IDispatcher>(), dialogs, services, characters));
        // ShowRuns does not wait for the first read, and a selection made before it lands is lost with it.
        await shown.LoadAsync();
        await shown.ShowActivityStatsAsync(key);
    }

    public Task OpenSharingSettingsAsync() => SettingsOpener?.Invoke(Views.SettingsWindow.PrivacyCategory) ?? Task.CompletedTask;
}
