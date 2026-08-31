using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The Appraisal tool: paste what is in a hangar or a wreck, get what it is worth. The paste is read by the
/// inventory parser built in ET-57, which until now had no caller at all; the names it finds become type ids
/// through the SDE and the ids are valued by an <see cref="IAppraisalProvider"/>.
///
/// Two things are deliberately said out loud rather than left to be inferred from a number. A name the SDE does not
/// know is listed separately instead of quietly dropping out of the total, and an empty price cache is reported as
/// such — a total of zero looks like an answer.
/// </summary>
public partial class AppraisalViewModel : ViewModelBase
{
    private const string PastePrompt = "Paste an inventory listing (Ctrl+A, Ctrl+C in an EVE inventory window).";

    private readonly ISdeAccessor _sde;
    private readonly IDialogService? _dialogs;

    public AppraisalViewModel(IEnumerable<IAppraisalProvider> providers, ISdeAccessor sde, IDialogService? dialogs = null)
    {
        _sde = sde;
        _dialogs = dialogs;
        Providers = [.. providers.OrderBy(provider => provider.DisplayName, StringComparer.Ordinal)];
        SelectedProvider = Providers.FirstOrDefault();
    }

    /// <summary>The price sources that are installed. The picker for them stays hidden while there is only one —
    /// this is the only place in the tool that knows providers can be plural.</summary>
    public IReadOnlyList<IAppraisalProvider> Providers { get; }

    [ObservableProperty] private IAppraisalProvider? _selectedProvider;

    public bool ShowProviderPicker => Providers.Count > 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AppraiseCommand))]
    private string _pasteText = string.Empty;

    public ObservableCollection<AppraisalRowViewModel> Rows { get; } = [];

    /// <summary>The pasted names that resolved to nothing — on screen as a list of their own, never merged away.</summary>
    public ObservableCollection<string> Unresolved { get; } = [];

    [ObservableProperty] private string _status = PastePrompt;

    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AppraiseCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _totalDisplay = IskFormat.Exact(0);

    /// <summary>The total behind <see cref="TotalDisplay"/> — kept only to say whether there is one worth copying at
    /// all; the copy itself reads <see cref="TotalDisplay"/>, not this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTotal))]
    [NotifyCanExecuteChangedFor(nameof(CopyTotalCommand))]
    private double _totalRaw;

    /// <summary>What the prices on screen are and when they are from, in the provider's own words.</summary>
    [ObservableProperty] private string _pricingBasis = string.Empty;

    public bool HasRows => Rows.Count > 0;

    /// <summary>Whether there is a real total to copy. False for the empty screen and for a listing whose rows
    /// are all priceless — both read "— ISK" on screen, and copying either would silently hand out that placeholder
    /// as if it were a figure.</summary>
    public bool HasTotal => TotalRaw > 0;

    public bool HasUnresolved => Unresolved.Count > 0;

    /// <summary>Counted only once there is something to count — "ITEMS (0)" beside a total of nothing reads as an
    /// answer to a question that was never asked.</summary>
    public string RowsHeader => HasRows ? $"ITEMS ({Rows.Count})" : "ITEMS";

    public string UnresolvedHeader => $"NOT RECOGNISED ({Unresolved.Count})";

    private bool CanAppraise => !IsBusy && !string.IsNullOrWhiteSpace(PasteText);

    [RelayCommand(CanExecute = nameof(CanAppraise))]
    private async Task AppraiseAsync(CancellationToken cancellationToken)
    {
        // Every run starts from an empty screen: the box has changed, so the figures standing there no longer
        // describe what is in it, and a total left behind beside a refusal is the worse of the two mistakes.
        _Show([], [], string.Empty, 0);

        if (SelectedProvider is not { } provider)
        {
            _Fail("No price source is available.");
            return;
        }

        if (!_sde.IsAvailable)
        {
            _Fail("The SDE is not loaded yet, so item names cannot be looked up. Import it from Settings first.");
            return;
        }

        IsBusy = true;
        try
        {
            var parsed = ClipboardInventoryParser.Parse(PasteText);
            if (parsed.Count == 0)
            {
                _Fail("That does not read as an inventory listing. The rows have to be the tab-separated ones EVE "
                      + "copies out of an inventory window.");
                return;
            }

            List<AppraisalLine> lines = [];
            List<string> unresolved = [];
            foreach (var item in parsed)
            {
                if (_sde.TryGetTypeId(item.Name, out var typeId))
                    lines.Add(new AppraisalLine(typeId, item.Name, item.Quantity ?? 1));   // no quantity column = one of it
                else
                    unresolved.Add(item.Name);
            }

            if (lines.Count == 0)
            {
                _Show([], unresolved, string.Empty, 0);   // nothing valued, but say what was read
                // Where a multibuy list ("Tritanium 100") lands: it reads as one name column, and none of those
                // names is a type. Saying so beats a bare "nothing found" on the format most likely to be tried.
                _Fail($"None of the {unresolved.Count} pasted names is a known item type. A multibuy list (name and "
                      + "amount separated by a space) is not read yet — copy the rows from an inventory window.");
                return;
            }

            var result = await provider.AppraiseAsync(lines, cancellationToken);
            if (!result.IsSuccess || result.Value is not { } outcome)
            {
                _Show([], unresolved, string.Empty, 0);
                _Fail(result.Messages.FirstOrDefault()?.Text ?? "The price source returned nothing.");
                return;
            }

            _Show(outcome.Rows, [.. unresolved, .. outcome.Unresolved], outcome.PricingBasis, outcome.Total);

            var priceless = outcome.Rows.Count(row => row.Price is null);
            Status = $"{outcome.Rows.Count} item(s) valued via {provider.DisplayName}."
                     + (priceless > 0 ? $" {priceless} of them carry no price." : string.Empty)
                     + (unresolved.Count > 0 ? $" {unresolved.Count} name(s) were not recognised." : string.Empty);
            StatusIsError = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Empties the box and everything read out of it, so the next paste is not appraised beside the last.</summary>
    [RelayCommand]
    private void Clear()
    {
        PasteText = string.Empty;
        _Show([], [], string.Empty, 0);
        Status = PastePrompt;
        StatusIsError = false;
    }

    private void _Show(IReadOnlyList<AppraisalRow> rows, IReadOnlyList<string> unresolved, string basis, double total)
    {
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(new AppraisalRowViewModel(row));

        Unresolved.Clear();
        foreach (var name in unresolved)
            Unresolved.Add(name);

        PricingBasis = basis;
        TotalRaw = total;
        TotalDisplay = IskFormat.Exact(total);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasUnresolved));
        OnPropertyChanged(nameof(RowsHeader));
        OnPropertyChanged(nameof(UnresolvedHeader));
    }

    private void _Fail(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    /// <summary>Puts the total on the clipboard exactly as it reads on screen (operator's call: pasted into a chat,
    /// not a spreadsheet, so the grouping and the "ISK" suffix stay) — the same <see cref="TotalDisplay"/> string,
    /// not a second formatting rule that could drift from it. Guarded by <see cref="HasTotal"/>: with nothing valued
    /// (or nothing priced) there is no total to copy, so the button is not offered rather than silently handing out
    /// "— ISK".</summary>
    [RelayCommand(CanExecute = nameof(HasTotal))]
    private async Task CopyTotalAsync()
    {
        if (_dialogs is null) return;
        await _dialogs.SetClipboardTextAsync(TotalDisplay);
        Status = "Copied the total to the clipboard.";
        StatusIsError = false;
    }
}
