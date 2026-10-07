using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One item row of the killmail detail screen's FIT section (ET-333): icon, name, quantity and the ISK that
/// dropped and that was destroyed, each in its own column. A stack that is partly dropped and partly destroyed is
/// two <see cref="KillmailDetailItemLineDto"/> lines but one row, with an amount in both columns (ET-475).</summary>
public sealed partial class KillmailDetailItemRowViewModel : ObservableObject
{
    public KillmailDetailItemRowViewModel(IReadOnlyList<KillmailDetailItemLineDto> lines, string name, string? metaHint,
        bool isTopValue)
    {
        KillmailDetailItemLineDto? dropped = lines.FirstOrDefault(line => !line.IsDestroyed);
        KillmailDetailItemLineDto? destroyed = lines.FirstOrDefault(line => line.IsDestroyed);

        Name = name;
        TypeId = lines[0].TypeId;
        Initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        MetaHint = metaHint;
        QuantityText = IskFormat.Number(lines.Sum(line => line.Quantity));
        DroppedText = _AmountText(dropped);
        DestroyedText = _AmountText(destroyed);
        IsTopValue = isTopValue;
        Value = _Sum(lines);
    }

    private static string _AmountText(KillmailDetailItemLineDto? line) =>
        line is null ? string.Empty : IskFormat.NumberOrNoPrice(line.Value);

    private static decimal? _Sum(IEnumerable<KillmailDetailItemLineDto> lines)
    {
        List<decimal> known = [.. lines.Select(line => line.Value).OfType<decimal>()];
        return known.Count == 0 ? null : known.Sum();
    }

    public string Name { get; }

    public string Initial { get; }

    public int TypeId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    public async Task LoadIconAsync(ITypeImageProvider images) =>
        Icon = await images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);

    /// <summary>"loaded" for a charge sitting in a slot, null otherwise.</summary>
    public string? MetaHint { get; }

    public string QuantityText { get; }

    /// <summary>Empty when nothing of the stack dropped.</summary>
    public string DroppedText { get; }

    /// <summary>Empty when nothing of the stack was destroyed.</summary>
    public string DestroyedText { get; }

    /// <summary>The single highest-value row in its own group, highlighted like <c>ActivityLootLineViewModel</c>'s
    /// own top-value loot line.</summary>
    public bool IsTopValue { get; }

    internal decimal? Value { get; }
}
