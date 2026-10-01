using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace EveUtils.Client.Opsec;

/// <summary>Marks a bound location for a view that binds a shared DTO directly, where no view model builds the text
/// to mark it in (ET-417). Runs before a binding's <c>StringFormat</c>, so "Selected: {0}" keeps its own words.</summary>
public sealed class OpsecMarkConverter : IValueConverter
{
    public static readonly OpsecMarkConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? null : OpsecText.Mark(System.Convert.ToString(value, culture));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("OPSEC marking is one-way: a masked location is never typed back.");
}
