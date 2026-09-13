using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace EveUtils.Client.Controls;

/// <summary>
/// The fixed colours of the combat figures, the same in every faction theme (ET-277). DPS out used to take the
/// faction accent, which put it beside cap in Caldari (both blue) and beside reps in Gallente (both green). Every
/// pair that shares a graph lane passes the dataviz validator on the panel surface — red IN against aqua REPS in the
/// hp/s lane, violet NEUT against amber CAP in the GJ/s lane — and amber and red, which would not, never share one.
/// OUT is the panel's own bright text colour: the figure the meter is about, not one hue among the others.
/// XAML reads the brushes through <c>x:Static</c>, so code and markup cannot drift apart.
/// </summary>
public static class CombatInk
{
    public static readonly Color OutColor = Color.Parse("#FFF3ECE0");
    public static readonly Color InColor = Color.Parse("#FFEF5A5A");
    public static readonly Color RepColor = Color.Parse("#FF199E70");
    public static readonly Color NeutColor = Color.Parse("#FF9085E9");
    public static readonly Color CapColor = Color.Parse("#FFC98500");

    public static readonly IImmutableSolidColorBrush Out = new ImmutableSolidColorBrush(OutColor);
    public static readonly IImmutableSolidColorBrush In = new ImmutableSolidColorBrush(InColor);
    public static readonly IImmutableSolidColorBrush Rep = new ImmutableSolidColorBrush(RepColor);
    public static readonly IImmutableSolidColorBrush Neut = new ImmutableSolidColorBrush(NeutColor);
    public static readonly IImmutableSolidColorBrush Cap = new ImmutableSolidColorBrush(CapColor);
}
