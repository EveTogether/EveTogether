using System.Globalization;
using System.Text.Json;

namespace EveUtils.Server.Backup;

/// <summary>
/// Converts one column value between the database and its JSON form in the archive.
///
/// Everything here is <see cref="CultureInfo.InvariantCulture"/> and round-trip formats: an archive taken on a
/// Dutch machine has to restore on an American one, and a decimal separator that follows the operating system
/// would corrupt exactly the numbers nobody re-checks (ET-34).
///
/// The declared <see cref="BackupColumnType"/> — not the runtime type of the value — decides the encoding. ADO.NET
/// providers disagree about what a column hands back (SQLite returns <c>long</c> for a bool, MySQL an
/// <c>sbyte</c>, a GUID arrives as bytes or as text), so both directions normalise onto the declared type and the
/// archive reads the same whichever of the four providers wrote it.
/// </summary>
internal static class BackupValueCodec
{
    public static void Write(Utf8JsonWriter writer, object? value, BackupColumnType type, string column)
    {
        if (value is null or DBNull)
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            _WriteValue(writer, value, type);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Column '{column}' holds a {value.GetType().Name} that does not fit its declared backup type {type}.", ex);
        }
    }

    public static object? Read(JsonElement element, BackupColumnType type, string column)
    {
        if (element.ValueKind is JsonValueKind.Null)
            return null;

        try
        {
            return _ReadValue(element, type);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"The backup archive holds a value for column '{column}' that is not a valid {type}.", ex);
        }
    }

    /// <summary>Maps a provider's column CLR type onto the archive's closed type list. Null when the type has no
    /// representation here — the caller reports which table and column, and the export stops.</summary>
    public static BackupColumnType? FromClrType(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);

        if (type == typeof(bool)) return BackupColumnType.Boolean;
        if (type == typeof(byte) || type == typeof(sbyte)) return BackupColumnType.Byte;
        if (type == typeof(short) || type == typeof(ushort)) return BackupColumnType.Int16;
        if (type == typeof(int) || type == typeof(uint)) return BackupColumnType.Int32;
        if (type == typeof(long)) return BackupColumnType.Int64;
        if (type == typeof(decimal)) return BackupColumnType.Decimal;
        if (type == typeof(double)) return BackupColumnType.Double;
        if (type == typeof(float)) return BackupColumnType.Single;
        if (type == typeof(string) || type == typeof(char)) return BackupColumnType.String;
        if (type == typeof(Guid)) return BackupColumnType.Guid;
        if (type == typeof(DateTime)) return BackupColumnType.DateTime;
        if (type == typeof(DateTimeOffset)) return BackupColumnType.DateTimeOffset;
        if (type == typeof(TimeSpan)) return BackupColumnType.TimeSpan;
        if (type == typeof(byte[])) return BackupColumnType.Bytes;

        // ulong is the one integral type left out on purpose: it does not survive a round trip through the signed
        // 64-bit values every supported provider stores, and no column in this model uses it.
        return null;
    }

    private static void _WriteValue(Utf8JsonWriter writer, object value, BackupColumnType type)
    {
        switch (type)
        {
            case BackupColumnType.Boolean:
                writer.WriteBooleanValue(Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.Byte:
            case BackupColumnType.Int16:
            case BackupColumnType.Int32:
            case BackupColumnType.Int64:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            // Written as text, not as a JSON number: a JSON number is a double to most readers, and that quietly
            // rounds money and coordinates.
            case BackupColumnType.Decimal:
                writer.WriteStringValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.Double:
                writer.WriteStringValue(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.Single:
                writer.WriteStringValue(Convert.ToSingle(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.String:
                writer.WriteStringValue(value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.Guid:
                writer.WriteStringValue(_ToGuid(value).ToString("D", CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.DateTime:
                writer.WriteStringValue(_ToDateTime(value).ToString("O", CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.DateTimeOffset:
                writer.WriteStringValue(_ToDateTimeOffset(value).ToString("O", CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.TimeSpan:
                writer.WriteStringValue(_ToTimeSpan(value).ToString("c", CultureInfo.InvariantCulture));
                break;
            case BackupColumnType.Bytes:
                writer.WriteBase64StringValue((byte[])value);
                break;
            default:
                throw new InvalidOperationException($"Backup column type {type} has no encoding.");
        }
    }

    private static object _ReadValue(JsonElement element, BackupColumnType type) => type switch
    {
        BackupColumnType.Boolean => element.GetBoolean(),
        BackupColumnType.Byte => (byte)element.GetInt64(),
        BackupColumnType.Int16 => (short)element.GetInt64(),
        BackupColumnType.Int32 => (int)element.GetInt64(),
        BackupColumnType.Int64 => element.GetInt64(),
        BackupColumnType.Decimal => decimal.Parse(_Text(element), CultureInfo.InvariantCulture),
        BackupColumnType.Double => double.Parse(_Text(element), CultureInfo.InvariantCulture),
        BackupColumnType.Single => float.Parse(_Text(element), CultureInfo.InvariantCulture),
        BackupColumnType.String => _Text(element),
        BackupColumnType.Guid => Guid.Parse(_Text(element), CultureInfo.InvariantCulture),
        BackupColumnType.DateTime => DateTime.Parse(_Text(element), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        BackupColumnType.DateTimeOffset => DateTimeOffset.Parse(_Text(element), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        BackupColumnType.TimeSpan => TimeSpan.Parse(_Text(element), CultureInfo.InvariantCulture),
        BackupColumnType.Bytes => element.GetBytesFromBase64(),
        _ => throw new InvalidDataException($"Backup column type {type} has no decoding."),
    };

    private static string _Text(JsonElement element) =>
        element.GetString() ?? throw new FormatException("Expected a JSON string.");

    private static Guid _ToGuid(object value) => value switch
    {
        Guid guid => guid,
        string text => Guid.Parse(text, CultureInfo.InvariantCulture),
        byte[] bytes => new Guid(bytes),
        _ => throw new InvalidCastException($"Cannot read a Guid from {value.GetType().Name}."),
    };

    private static DateTime _ToDateTime(object value) => value switch
    {
        DateTime dateTime => dateTime,
        DateTimeOffset offset => offset.UtcDateTime,
        string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => throw new InvalidCastException($"Cannot read a DateTime from {value.GetType().Name}."),
    };

    private static DateTimeOffset _ToDateTimeOffset(object value) => value switch
    {
        DateTimeOffset offset => offset,
        // A provider that has no offset type (MySQL) hands back the UTC instant EF stored; re-attaching the zero
        // offset is what EF's own value converter does on the way out.
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => throw new InvalidCastException($"Cannot read a DateTimeOffset from {value.GetType().Name}."),
    };

    private static TimeSpan _ToTimeSpan(object value) => value switch
    {
        TimeSpan span => span,
        string text => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
        long ticks => TimeSpan.FromTicks(ticks),
        _ => throw new InvalidCastException($"Cannot read a TimeSpan from {value.GetType().Name}."),
    };
}
