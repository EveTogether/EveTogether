using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.Controls;

/// <summary>
/// Where an ISK figure came from, as one thin bar split by source (ET-290): bounty, loot, homefront payout, mission
/// rewards and mining, each in its own fixed ink (<c>Isk*Brush</c> in Themes/EveUtils.axaml). Fixed rather than taken
/// from the faction accent, so a colour means the same source in every theme. The tooltip names the source under the
/// pointer.
///
/// Consumables is left out: it is money spent, and a bar of what came in has no negative part to draw it as.
/// </summary>
public sealed class IskSourceBar : Control
{
    private static readonly IBrush Track = new ImmutableSolidColorBrush(Color.Parse("#0FFFFFFF"));

    private static readonly (IskSource Source, string BrushKey, string Label)[] Sources =
    [
        (IskSource.Bounty, "IskBountyBrush", "Bounty"),
        (IskSource.Loot, "IskLootBrush", "Loot"),
        (IskSource.HomefrontPayout, "IskPayoutBrush", "Homefront payout"),
        (IskSource.Rewards, "IskRewardBrush", "Mission rewards"),
        (IskSource.Mining, "IskMiningBrush", "Mining")
    ];

    public static readonly StyledProperty<IskBreakdown?> IskProperty =
        AvaloniaProperty.Register<IskSourceBar, IskBreakdown?>(nameof(Isk));

    static IskSourceBar()
    {
        AffectsRender<IskSourceBar>(IskProperty);
    }

    public IskBreakdown? Isk
    {
        get => GetValue(IskProperty);
        set => SetValue(IskProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var track = new Rect(Bounds.Size);
        if (track.Width <= 0 || track.Height <= 0)
            return;

        context.DrawRectangle(Track, null, track, 1, 1);
        (IskSource Source, string BrushKey, string Label, decimal Amount)[] parts = _Parts();
        decimal total = parts.Sum(part => part.Amount);
        if (total <= 0)
            return;

        using (context.PushClip(new RoundedRect(track, 1)))
        {
            double x = 0;
            foreach ((_, string brushKey, _, decimal amount) in parts)
            {
                double width = (double)(amount / total) * track.Width;
                if (this.TryFindResource(brushKey, out object? brush) && brush is IBrush ink)
                    context.DrawRectangle(ink, null, new Rect(x, 0, width, track.Height));
                x += width;
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IskProperty)
            ToolTip.SetTip(this, _Summary());
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        (IskSource Source, string BrushKey, string Label, decimal Amount)[] parts = _Parts();
        decimal total = parts.Sum(part => part.Amount);
        if (total <= 0 || Bounds.Width <= 0)
            return;

        double pointer = e.GetPosition(this).X / Bounds.Width;
        double start = 0;
        foreach ((_, _, string label, decimal amount) in parts)
        {
            double end = start + (double)(amount / total);
            if (pointer <= end)
            {
                ToolTip.SetTip(this, $"{label} · {IskFormat.Whole(amount)}");
                return;
            }
            start = end;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetTip(this, _Summary());
    }

    private (IskSource Source, string BrushKey, string Label, decimal Amount)[] _Parts() =>
        Isk is { } isk
            ? [.. Sources
                .Select(source => (source.Source, source.BrushKey, source.Label,
                    Amount: isk.Of(source.Source) is { Certainty: not IskCertainty.Unknown, Amount: > 0 } part ? part.Amount : 0m))
                .Where(part => part.Amount > 0)]
            : [];

    private string _Summary()
    {
        string[] lines = [.. _Parts().Select(part => $"{part.Label} · {IskFormat.Whole(part.Amount)}")];
        return lines.Length == 0 ? "Nothing valued" : string.Join(Environment.NewLine, lines);
    }
}
