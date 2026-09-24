using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One run's CONSUMABLES on the detail screen (ET-334): what its own pilot spent — the filament and anything written
/// out beside it — and the way to rewrite that by hand, the way LOOT's block rewrites loot. A pilot who went in on a
/// fleetmate's filament sets his own to nothing here; nothing moves to the fleetmate, who records his own.
/// </summary>
public sealed partial class ConsumablesCharacterViewModel : ObservableObject
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly ISdeAccessor? _sde;
    private readonly Func<IReadOnlyList<int>, Task> _stored;

    /// <param name="stored">Awaited once a rewritten list is stored, with the type ids it holds — so the owner can
    /// price what is new on it before the screen reads the totals it moved again.</param>
    public ConsumablesCharacterViewModel(CqrsDispatcher dispatcher, ISdeAccessor? sde, Guid runId, string characterText,
        bool isReadOnly, Func<IReadOnlyList<int>, Task> stored)
    {
        _dispatcher = dispatcher;
        _sde = sde;
        _stored = stored;
        RunId = runId;
        CharacterText = characterText;
        IsReadOnly = isReadOnly;
        Editor = new InventoryListEditorViewModel(sde, _StoreAsync, acceptsEmpty: true);
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(InventoryListEditorViewModel.IsOpen))
                return;

            OnPropertyChanged(nameof(CanOfferEdit));
            OnPropertyChanged(nameof(CanEdit));
        };
    }

    public Guid RunId { get; }

    public string CharacterText { get; }

    /// <summary>Someone else's run, pulled in from a server: only its own pilot can say what he spent.</summary>
    public bool IsReadOnly { get; }

    public ObservableCollection<ActivityLootLineViewModel> Lines { get; } = [];

    public InventoryListEditorViewModel Editor { get; }

    [ObservableProperty] private string _subtotalText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    public bool HasLines => Lines.Count > 0;

    public bool CanOfferEdit => !IsReadOnly && !Editor.IsOpen && _sde is not null;

    public bool CanEdit => CanOfferEdit && !IsBusy;

    public void Show(IReadOnlyList<ActivityLootLineViewModel> lines)
    {
        Lines.Clear();
        foreach (ActivityLootLineViewModel line in lines)
        {
            line.IsAlternate = Lines.Count % 2 == 1;
            Lines.Add(line);
        }

        decimal[] priced = [.. lines.Select(line => line.Value).OfType<decimal>()];
        SubtotalText = lines.Count == 0
            ? "nothing spent"
            : IskFormat.WholeOrNoPrice(priced.Length == 0 ? null : -priced.Sum());
        OnPropertyChanged(nameof(HasLines));
    }

    partial void OnIsBusyChanged(bool value) => Editor.IsBusy = value;

    [RelayCommand]
    private void BeginEdit() => Editor.Open(_AsPasteText());

    /// <summary>The list in the form EVE itself copies, so a row can be pasted in from the game beside the ones already
    /// there. A filament the SDE could not name at SAVE has no type to match back to the run's own count, so it is
    /// left out rather than written back as a second filament beside that count.</summary>
    private string _AsPasteText() => string.Join(Environment.NewLine, Lines
        .Where(line => line.ItemTypeId > 0)
        .Select(line => $"{line.Name}\t{(line.Quantity ?? 1).ToString(CultureInfo.InvariantCulture)}"));

    private async Task<string?> _StoreAsync(InventoryTextReading reading, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            Result stored = await _dispatcher.Send(new SetRunConsumablesManualCommand(RunId,
                [.. reading.Lines.Select(resolved => new RunLootEntryInput
                {
                    ItemTypeId = resolved.Line.TypeId,
                    Name = resolved.Line.Name,
                    Quantity = resolved.Line.Quantity,
                    Volume = resolved.Item.Volume,
                    ClipboardPrice = resolved.Item.Price,
                    LootKind = LootKind.Lost
                })]), cancellationToken);
            if (!stored.IsSuccess)
                return stored.Messages.Count > 0 ? stored.Messages[0].Text : "This list was not stored.";

            Editor.Close();
        }
        finally
        {
            IsBusy = false;
        }

        await _stored([.. reading.Lines.Select(resolved => resolved.Line.TypeId)]);
        return null;
    }
}
