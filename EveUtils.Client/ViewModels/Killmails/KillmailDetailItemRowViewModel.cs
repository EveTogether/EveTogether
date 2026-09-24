using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One FIT-section item line on the killmail detail screen (ET-333) — the loot-line look
/// (icon, name, quantity, value), plus a DESTROYED/DROPPED badge. A stack that is partly destroyed and partly
/// dropped is already two <see cref="KillmailDetailItemLineDto"/> lines by the time it reaches this row.</summary>
public sealed class KillmailDetailItemRowViewModel
{
    public KillmailDetailItemRowViewModel(KillmailDetailItemLineDto line, string name, string? metaHint, bool isTopValue)
    {
        Name = name;
        Initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        MetaHint = metaHint;
        IsDestroyed = line.IsDestroyed;
        BadgeText = line.IsDestroyed ? "DESTROYED" : "DROPPED";
        QuantityText = line.Quantity.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        ValueText = line.Value is { } value ? IskFormat.Compact(value) : "no price";
        IsTopValue = isTopValue;
        Value = line.Value;
    }

    public string Name { get; }

    public string Initial { get; }

    /// <summary>"loaded" for a charge sitting in a slot, "cargo" for an item in the cargo hold, null otherwise.</summary>
    public string? MetaHint { get; }

    public bool IsDestroyed { get; }

    public string BadgeText { get; }

    public string QuantityText { get; }

    public string ValueText { get; }

    /// <summary>The single highest-value row in its own group, highlighted like <c>ActivityLootLineViewModel</c>'s
    /// own top-value loot line.</summary>
    public bool IsTopValue { get; }

    internal decimal? Value { get; }
}
