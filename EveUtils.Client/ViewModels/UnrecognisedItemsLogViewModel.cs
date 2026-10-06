using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The log of item names no SDE type carried when they were entered (ET-460): what is still waiting to be priced, with
/// how much of it there is, in how many runs, and since when. By default only the open ones; the resolved filter shows
/// what was recognised since, and when. A new SDE or price refresh works through the list by itself — the button is for
/// not waiting for it.
/// </summary>
public sealed partial class UnrecognisedItemsLogViewModel(IServiceScopeFactory scopes, UnrecognisedLootRepricer repricer)
    : ViewModelBase, ISingletonService
{
    public ObservableCollection<UnrecognisedItemRowViewModel> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => ShowResolved
        ? "Nothing has been recognised since it was logged."
        : "Every item name that was entered is known. Names the EVE static data does not know yet show up here.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private bool _showResolved;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _statusText;

    partial void OnShowResolvedChanged(bool value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        using IServiceScope scope = scopes.CreateScope();
        Result<IReadOnlyList<UnrecognisedLootItemDto>> items = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query(new GetUnrecognisedLootQuery(ShowResolved ? UnrecognisedItemStatus.Resolved : UnrecognisedItemStatus.Open));
        Rows.Clear();
        if (items.IsSuccess && items.Value is { } found)
            foreach (UnrecognisedLootItemDto item in found)
                Rows.Add(new UnrecognisedItemRowViewModel(item));

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task RepriceAsync()
    {
        IsBusy = true;
        try
        {
            int recognised = await repricer.RepriceAsync();
            StatusText = recognised switch
            {
                0 => "Nothing new was recognised.",
                1 => "1 line was recognised and priced.",
                _ => $"{recognised} lines were recognised and priced."
            };
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
