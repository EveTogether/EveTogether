using System;
using System.Collections.Generic;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// Opens the Appraisal tool — from the Tools menu, and with one blueprint in its BLUEPRINTS mode from a loot line's "Open
/// in appraisal" (ET-502). One place builds the tool, so both ways in get the same one. A fresh view-model per open, so
/// it reads the price cache as it stands now; an open tool is reused by the module host.
/// </summary>
public sealed class AppraisalOpener(IServiceProvider services) : ISingletonService
{
    public void Open(int? blueprintTypeId = null)
    {
        if (services.GetService<IDialogService>() is not { } dialogs)
            return;

        ISdeAccessor sde = services.GetRequiredService<ISdeAccessor>();
        BlueprintAppraisalViewModel? blueprints = services.GetService<IBlueprintAppraisalService>() is { } appraisals
            ? new BlueprintAppraisalViewModel(appraisals, sde, dialogs, services.GetService<ITypeImageProvider>())
            : null;
        dialogs.ShowAppraisal(new AppraisalViewModel(
            services.GetRequiredService<IEnumerable<IAppraisalProvider>>(),
            sde,
            dialogs,
            services.GetService<IAppraisalProviderSelector>(),
            services.GetService<IEveWorkbenchKeyStore>(),
            services.GetService<IDispatcher>(),
            blueprints), blueprintTypeId);
    }
}
