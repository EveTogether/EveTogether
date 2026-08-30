using System;
using System.Collections.Generic;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Reads the rows of a recognised inventory listing. Nothing is read by position: the player chooses which
/// columns the inventory window shows, so a column is identified by the shape of its contents — a volume ends in
/// <c>m3</c>, a price in <c>ISK</c>, a quantity is a bare whole number, and the name is the text column that is
/// always filled. Numbers come in the player's locale (<c>42.237,65</c> here, <c>42,237.65</c> elsewhere), which
/// is why the form is derived from the text itself rather than from any <see cref="System.Globalization.CultureInfo"/>:
/// a culture-blind parse would return a silently wrong number instead of an error. Anything that cannot be read
/// with certainty yields no value rather than a guess. Reasoning and the measurements behind it: docs/clipboard.md.
/// </summary>
public static class ClipboardInventoryParser
{
    public static IReadOnlyList<ClipboardInventoryItem> Parse(string text)
    {
        var rows = ReadRows(text);
        if (rows.Count == 0)
            return [];

        var nameColumn = FindNameColumn(rows);
        if (nameColumn is null)
            return [];

        var quantityColumn = FindQuantityColumn(rows);
        var volumeColumn = FindUnitColumn(rows, "m3");
        var priceColumn = FindUnitColumn(rows, "ISK");
        var items = new List<ClipboardInventoryItem>(rows.Count);

        foreach (var row in rows)
        {
            long? quantity = quantityColumn is { } quantityIndex && TryParseWholeNumber(row[quantityIndex], out var parsedQuantity)
                ? parsedQuantity
                : null;
            decimal? volume = volumeColumn is { } volumeIndex && TryParseUnitNumber(row[volumeIndex], "m3", out var parsedVolume)
                ? parsedVolume
                : null;
            decimal? price = priceColumn is { } priceIndex && TryParseUnitNumber(row[priceIndex], "ISK", out var parsedPrice)
                ? parsedPrice
                : null;

            items.Add(new ClipboardInventoryItem(row[nameColumn.Value].Trim(), quantity, volume, price));
        }

        return items;
    }

    private static List<string[]> ReadRows(string text)
    {
        var rows = new List<string[]>();
        var columns = -1;

        foreach (var line in text.Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(row))
                continue;

            var fields = row.Split('\t');
            if (columns < 0)
                columns = fields.Length;
            else if (columns != fields.Length)
                return [];

            rows.Add(fields);
        }

        return rows;
    }

    private static int? FindNameColumn(IReadOnlyList<string[]> rows)
    {
        int? nameColumn = null;
        var highestDistinctCount = 0;
        var nextHighestDistinctCount = 0;

        for (var column = 0; column < rows[0].Length; column++)
        {
            var hasName = true;
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var field = row[column];
                if (string.IsNullOrWhiteSpace(field)
                    || IsUnitField(field, "m3")
                    || IsUnitField(field, "ISK")
                    || TryParseWholeNumber(field, out _))
                {
                    hasName = false;
                    break;
                }

                values.Add(field.Trim());
            }

            if (!hasName)
                continue;

            if (values.Count > highestDistinctCount)
            {
                nameColumn = column;
                nextHighestDistinctCount = highestDistinctCount;
                highestDistinctCount = values.Count;
            }
            else if (values.Count > nextHighestDistinctCount)
            {
                nextHighestDistinctCount = values.Count;
            }
        }

        // This chosen, unmeasured 2x guard comes from one 39-versus-10 capture; it rejects valid 15-versus-8 selections rather than misnaming them.
        return nameColumn is null || highestDistinctCount < nextHighestDistinctCount * 2 ? null : nameColumn;
    }

    private static int? FindQuantityColumn(IReadOnlyList<string[]> rows)
    {
        for (var column = 0; column < rows[0].Length; column++)
        {
            var hasQuantity = false;
            foreach (var row in rows)
            {
                var field = row[column];
                if (string.IsNullOrWhiteSpace(field))
                    continue;

                hasQuantity |= TryParseWholeNumber(field, out _);
            }

            if (hasQuantity)
                return column;
        }

        return null;
    }

    private static int? FindUnitColumn(IReadOnlyList<string[]> rows, string unit)
    {
        for (var column = 0; column < rows[0].Length; column++)
        {
            var hasUnit = false;
            var isUnitColumn = true;
            foreach (var row in rows)
            {
                var field = row[column];
                if (string.IsNullOrWhiteSpace(field))
                    continue;

                if (!IsUnitField(field, unit))
                {
                    isUnitColumn = false;
                    break;
                }

                hasUnit = true;
            }

            if (isUnitColumn && hasUnit)
                return column;
        }

        return null;
    }

    private static bool IsUnitField(string field, string unit) => field.TrimEnd().EndsWith(unit, StringComparison.Ordinal);

    private static bool TryParseUnitNumber(string field, string unit, out decimal number)
    {
        number = default;
        var value = field.Trim();
        if (!value.EndsWith(unit, StringComparison.Ordinal))
            return false;

        return TryParseLocalNumber(value[..^unit.Length].TrimEnd(), out number);
    }

    private static bool TryParseWholeNumber(string field, out long number)
    {
        number = default;
        var value = field.Trim();
        if (value.Length == 0)
            return false;

        try
        {
            var separator = '\0';
            var digitsInGroup = 0;
            var hasSeparator = false;

            for (var index = value.Length - 1; index >= 0; index--)
            {
                var character = value[index];
                if (character is ',' or '.')
                {
                    if ((separator != '\0' && separator != character) || digitsInGroup != 3)
                        return false;

                    separator = character;
                    hasSeparator = true;
                    digitsInGroup = 0;
                    continue;
                }

                if (character is < '0' or > '9')
                    return false;

                digitsInGroup++;
            }

            if (hasSeparator && digitsInGroup is < 1 or > 3)
                return false;

            foreach (var character in value)
            {
                if (character is ',' or '.')
                    continue;

                number = checked(number * 10 + character - '0');
            }

            return true;
        }
        catch (OverflowException)
        {
            number = default;
            return false;
        }
    }

    private static bool TryParseLocalNumber(string value, out decimal number)
    {
        number = default;
        if (value.Length == 0)
            return false;

        var comma = value.LastIndexOf(',');
        var dot = value.LastIndexOf('.');
        var decimalIndex = -1;
        var fractionDigits = 0;

        if (comma >= 0 && dot >= 0)
        {
            decimalIndex = Math.Max(comma, dot);
            fractionDigits = value.Length - decimalIndex - 1;
            if (fractionDigits == 0 || !HasValidGroups(value, decimalIndex))
                return false;
        }
        else if (comma >= 0 || dot >= 0)
        {
            var separator = comma >= 0 ? ',' : '.';
            var separators = Count(value, separator);
            var lastSeparator = Math.Max(comma, dot);
            var digitsAfterSeparator = value.Length - lastSeparator - 1;

            if (digitsAfterSeparator is 1 or 2)
            {
                decimalIndex = lastSeparator;
                fractionDigits = digitsAfterSeparator;
                if (!HasValidGroups(value, decimalIndex))
                    return false;
            }
            else if (separators > 1 && digitsAfterSeparator == 3 && HasValidGroups(value, value.Length))
            {
                decimalIndex = -1;
            }
            else
            {
                return false;
            }
        }

        try
        {
            foreach (var character in value)
            {
                if (character is ',' or '.')
                    continue;
                if (character is < '0' or > '9')
                    return false;

                number = checked(number * 10 + character - '0');
            }

            for (var digit = 0; digit < fractionDigits; digit++)
                number /= 10;

            return true;
        }
        catch (OverflowException)
        {
            number = default;
            return false;
        }
    }

    private static bool HasValidGroups(string value, int endExclusive)
    {
        var separators = 0;
        var digits = 0;

        for (var index = endExclusive - 1; index >= 0; index--)
        {
            var character = value[index];
            if (character is ',' or '.')
            {
                if (digits != 3)
                    return false;

                separators++;
                digits = 0;
                continue;
            }

            if (character is < '0' or > '9')
                return false;

            digits++;
        }

        return separators == 0 ? digits > 0 : digits is >= 1 and <= 3;
    }

    private static int Count(string value, char character)
    {
        var count = 0;
        foreach (var current in value)
        {
            if (current == character)
                count++;
        }

        return count;
    }
}
