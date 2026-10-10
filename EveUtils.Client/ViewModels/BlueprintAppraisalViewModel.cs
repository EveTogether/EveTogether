using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The BLUEPRINTS mode of the Appraisal tool (ET-502): paste blueprints, set each one's runs, ME and TE, and see what
/// building them earns at EVE average prices — the same <see cref="IBlueprintAppraisalService"/> the loot valuation
/// uses (ET-501). The shopping list sums the materials of the checked blueprints; a blueprint that turns a profit when it
/// comes in starts checked (Jithran, 10-10).
/// </summary>
public sealed partial class BlueprintAppraisalViewModel : ViewModelBase
{
    private const string PastePrompt = "Paste blueprints from an inventory window (Ctrl+A, Ctrl+C), one per row.";

    public const string PricingBasis =
        "EVE average prices (ESI) only — no Jita or Amarr orders. Job cost: 9% of the estimated item value, a fixed assumption.";

    private readonly IBlueprintAppraisalService _appraisals;
    private readonly ISdeAccessor _sde;
    private readonly IDialogService? _dialogs;
    private readonly ITypeImageProvider? _images;

    public BlueprintAppraisalViewModel(IBlueprintAppraisalService appraisals, ISdeAccessor sde, IDialogService? dialogs = null,
        ITypeImageProvider? images = null)
    {
        _appraisals = appraisals;
        _sde = sde;
        _dialogs = dialogs;
        _images = images;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AppraiseCommand))]
    private string _pasteText = string.Empty;

    public ObservableCollection<BlueprintAppraisalRowViewModel> Rows { get; } = [];

    /// <summary>Pasted names that are no blueprint the SDE can build from — listed, never silently dropped.</summary>
    public ObservableCollection<string> Unresolved { get; } = [];

    public ObservableCollection<ShoppingListLineViewModel> ShoppingList { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private BlueprintAppraisalRowViewModel? _selectedRow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AppraiseCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = PastePrompt;

    [ObservableProperty] private bool _statusIsError;

    public bool HasRows => Rows.Count > 0;

    public bool HasSelection => SelectedRow is not null;

    public bool HasUnresolved => Unresolved.Count > 0;

    public bool HasShoppingList => ShoppingList.Count > 0;

    public string RowsHeader => HasRows ? $"BLUEPRINTS ({Rows.Count})" : "BLUEPRINTS";

    /// <summary>What building only the profitable blueprints earns.</summary>
    public string MaxProfitText => IskFormat.Whole(Rows.Where(row => row.IsProfitable).Sum(row => row.Profit ?? 0m));

    /// <summary>What building every priced blueprint earns, the loss-making ones included.</summary>
    public string BuildEverythingText => IskFormat.Whole(Rows.Where(row => row.HasPrice).Sum(row => row.Profit ?? 0m));

    public string LossMakingText
    {
        get
        {
            int losing = Rows.Count(row => row.IsLossMaking);
            int priceless = Rows.Count(row => !row.HasPrice);
            string text = $"{losing} blueprint{(losing == 1 ? "" : "s")}";
            return priceless > 0 ? $"{text} · {priceless} without a price" : text;
        }
    }

    public string CheckedText => $"{Rows.Count(row => row.IsChecked)} of {Rows.Count}";

    public string ShoppingListHeader => $"SHOPPING LIST · {Rows.Count(row => row.IsChecked)} CHECKED BLUEPRINT{(Rows.Count(row => row.IsChecked) == 1 ? "" : "S")}";

    private bool CanAppraise => !IsBusy && !string.IsNullOrWhiteSpace(PasteText);

    [RelayCommand(CanExecute = nameof(CanAppraise))]
    private async Task AppraiseAsync(CancellationToken cancellationToken)
    {
        _Clear();
        if (!_sde.IsAvailable)
        {
            _Fail("The SDE is not loaded yet, so blueprint names cannot be looked up. Import it from Settings first.");
            return;
        }

        IsBusy = true;
        try
        {
            (IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> lines, IReadOnlyList<string> unresolved) =
                SdeInventoryResolver.Resolve(ClipboardInventoryParser.Parse(PasteText), _sde);
            foreach (string name in unresolved)
                Unresolved.Add(name);
            foreach (int typeId in lines.Select(line => line.Line.TypeId).Distinct())
            {
                if (_sde.GetBlueprintManufacturing(typeId) is { } blueprint)
                    _Add(blueprint);
                else
                    Unresolved.Add($"{_NameOf(typeId)} — not a blueprint that builds anything");
            }

            await Task.WhenAll(Rows.Select(row => _AppraiseAsync(row, cancellationToken)));
            foreach (BlueprintAppraisalRowViewModel row in Rows)
                row.IsChecked = row.IsProfitable;
            SelectedRow = Rows.FirstOrDefault();
            _Refresh();
            if (Rows.Count == 0)
            {
                _Fail("None of the pasted names is a blueprint that builds something.");
                return;
            }
            Status = $"{Rows.Count} blueprint(s) valued at EVE average prices."
                     + (Unresolved.Count > 0 ? $" {Unresolved.Count} line(s) were not used." : string.Empty);
            StatusIsError = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Adds one blueprint and selects it — what "Open in appraisal" on a loot line does. A blueprint already in
    /// the list is selected rather than added twice; it starts checked when it turns a profit, like a pasted one.</summary>
    public async Task ShowBlueprintAsync(int blueprintTypeId, CancellationToken cancellationToken = default)
    {
        if (Rows.FirstOrDefault(row => row.TypeId == blueprintTypeId) is { } existing)
        {
            SelectedRow = existing;
            return;
        }
        if (_sde.GetBlueprintManufacturing(blueprintTypeId) is not { } blueprint)
        {
            _Fail($"{_NameOf(blueprintTypeId)} is not a blueprint that builds anything.");
            return;
        }

        BlueprintAppraisalRowViewModel row = _Add(blueprint);
        await _AppraiseAsync(row, cancellationToken);
        row.IsChecked = row.IsProfitable;
        SelectedRow = row;
        _Refresh();
        Status = $"{row.Name} valued at EVE average prices.";
        StatusIsError = false;
    }

    private bool CanCopyShoppingList => HasShoppingList;

    /// <summary>The list as EVE's multibuy window takes it: one "name amount" per line.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyShoppingList))]
    private async Task CopyShoppingListAsync()
    {
        if (_dialogs is null)
            return;
        await _dialogs.SetClipboardTextAsync(ShoppingListText());
        Status = $"Copied {ShoppingList.Count} material(s) to the clipboard — paste them into EVE's multibuy.";
        StatusIsError = false;
    }

    public string ShoppingListText() => string.Join(Environment.NewLine, ShoppingList.Select(line => line.MultibuyLine));

    [RelayCommand]
    private void Clear()
    {
        PasteText = string.Empty;
        _Clear();
        Status = PastePrompt;
        StatusIsError = false;
    }

    partial void OnSelectedRowChanged(BlueprintAppraisalRowViewModel? oldValue, BlueprintAppraisalRowViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
    }

    private BlueprintAppraisalRowViewModel _Add(SdeBlueprintManufacturing blueprint)
    {
        var row = new BlueprintAppraisalRowViewModel(blueprint, _NameOf(blueprint.BlueprintTypeId), _NameOf, _images);
        row.RebuildRequested += changed => Pending = _RebuildAsync(changed);
        row.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(BlueprintAppraisalRowViewModel.IsChecked))
                _Refresh();
        };
        Rows.Add(row);
        return row;
    }

    /// <summary>The appraisal a runs or ME change started, for a caller (a test) that waits for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>The icon loads of the shopping list's lines, for a caller (a test) that waits for them.</summary>
    public Task ShoppingIconsPending { get; private set; } = Task.CompletedTask;

    private async Task _RebuildAsync(BlueprintAppraisalRowViewModel row)
    {
        await _AppraiseAsync(row, CancellationToken.None);
        _Refresh();
    }

    private async Task _AppraiseAsync(BlueprintAppraisalRowViewModel row, CancellationToken cancellationToken)
    {
        if (await _appraisals.AppraiseAsync(row.TypeId, row.Runs, row.MaterialEfficiency, cancellationToken) is { } appraisal)
            row.Apply(appraisal);
    }

    private void _Refresh()
    {
        ShoppingList.Clear();
        foreach (ShoppingListLineViewModel line in Rows
                     .Where(row => row.IsChecked)
                     .Select(row => row.Appraisal)
                     .OfType<BlueprintAppraisal>()
                     .SelectMany(appraisal => appraisal.Materials)
                     .GroupBy(material => material.TypeId)
                     .Select(group => new ShoppingListLineViewModel(group.Key, _NameOf(group.Key), group.Sum(material => material.Needed)))
                     .OrderBy(line => line.Name, StringComparer.Ordinal))
        {
            ShoppingList.Add(line);
            ShoppingIconsPending = Task.WhenAll(ShoppingIconsPending, line.Icon.LoadAsync(_images));
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasUnresolved));
        OnPropertyChanged(nameof(HasShoppingList));
        OnPropertyChanged(nameof(RowsHeader));
        OnPropertyChanged(nameof(MaxProfitText));
        OnPropertyChanged(nameof(BuildEverythingText));
        OnPropertyChanged(nameof(LossMakingText));
        OnPropertyChanged(nameof(CheckedText));
        OnPropertyChanged(nameof(ShoppingListHeader));
        CopyShoppingListCommand.NotifyCanExecuteChanged();
    }

    private void _Clear()
    {
        SelectedRow = null;
        Rows.Clear();
        Unresolved.Clear();
        _Refresh();
    }

    private void _Fail(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    private string _NameOf(int typeId) => _sde.TryGetTypeName(typeId, out string name) ? name : $"type {typeId}";
}
