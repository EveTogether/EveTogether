using EveUtils.Client.Imaging;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Dialogs;

/// <summary>Builds the loot table window's view model and hands it to the dialog service, which reuses the window that is
/// already open (ET-519). A fresh view model per open reads the figures as they stand now.</summary>
public sealed class LootTableOpener(IServiceProvider services) : ILootTableOpener, ISingletonService
{
    public void Open(ActivityStatsKey? key, IReadOnlyList<int> characterIds)
    {
        if (services.GetService<IDialogService>() is not { } dialogs || services.GetService<ILootTableSource>() is not { } source)
            return;

        ISdeAccessor? sde = services.GetService<ISdeAccessor>();
        var viewModel = new LootTableViewModel(source, new ActivityStatsLabels(sde, new RunRowFacts(sde)), sde,
            services.GetService<ITypeImageProvider>());
        dialogs.ShowLootTable(viewModel, key, characterIds);
    }
}
