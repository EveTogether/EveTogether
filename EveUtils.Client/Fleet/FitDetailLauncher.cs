using System;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Imaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Opens the read-only radial fit-detail for a composition fit snapshot: deserialises the stored ESI JSON
/// and shows a <see cref="FitDetailWindowViewModel"/>. Shared by the fit picker, the composition editor and the
/// fleet roster — a snapshot has no local DB id, so the export/push/skill actions stay disabled (a pure read-only view).
/// </summary>
public static class FitDetailLauncher
{
    public static async Task OpenAsync(IServiceProvider services, IDialogService dialogs, FitReferenceInfo fit)
    {
        EsiFitting? esi;
        try { esi = JsonSerializer.Deserialize<EsiFitting>(fit.RawJson); }
        catch { esi = null; }

        if (esi is null)
        {
            await dialogs.ShowMessageAsync("Fit detail", "Could not read this fit.");
            return;
        }

        var viewModel = new FitDetailWindowViewModel(esi, FitNameResolverFactory.For(services),
            services.GetService<IFitStatsProvider>(),
            services.GetService<ISdeAccessor>(),
            services.GetService<IDogmaDataAccessor>(),
            services.GetService<ITypeImageProvider>(),
            services.GetService<IMarketPriceRepository>(),
            toasts: services.GetService<IToastService>());

        await viewModel.InitializeAsync();
        dialogs.ShowFitDetail(viewModel);
        _ = viewModel.LoadImagesAsync();   // opt-in CCP images pop in after the window shows
    }
}
