using System.Globalization;
using System.Text.Json;
using EveUtils.Server.Backup;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// Column values on their way in and out of an archive. Every case runs under a culture that formats numbers and
/// dates differently from the invariant one: an archive taken on the operator's Dutch machine has to restore
/// anywhere, and a decimal comma or a day-first date would corrupt precisely the values nobody re-reads (ET-34).
/// </summary>
public class BackupValueCodecTests
{
    // Declared as object so this public test class does not expose the internal BackupColumnType in a signature.
    public static TheoryData<object, object> Values => new()
    {
        { BackupColumnType.Boolean, true },
        { BackupColumnType.Byte, (byte)7 },
        { BackupColumnType.Int16, (short)-1234 },
        { BackupColumnType.Int32, 91000000 },
        { BackupColumnType.Int64, 9_007_199_254_740_993L },   // beyond what a double can hold exactly
        { BackupColumnType.Decimal, 1234.5678m },
        { BackupColumnType.Double, 1234.5678d },
        { BackupColumnType.Single, 1234.5f },
        { BackupColumnType.String, "Jita IV - Moon 4" },
        { BackupColumnType.Guid, Guid.Parse("2ab4c878-b1ea-25b6-bf58-d32896680cc4") },
        { BackupColumnType.DateTime, new DateTime(2026, 9, 1, 10, 11, 6, DateTimeKind.Utc) },
        { BackupColumnType.DateTimeOffset, new DateTimeOffset(2026, 9, 1, 10, 11, 6, 123, TimeSpan.FromHours(2)) },
        { BackupColumnType.TimeSpan, TimeSpan.FromMinutes(90) },
        { BackupColumnType.Bytes, new byte[] { 0x00, 0xFF, 0x10, 0x42 } },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void RoundTrip_UnderADutchCulture_ReturnsTheSameValue(object columnType, object value)
    {
        var type = (BackupColumnType)columnType;

        var restored = InDutchCulture(() => Decode(Encode(value, type), type));

        Assert.Equal(value, restored);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void RoundTrip_WrittenDutchAndReadInvariant_ReturnsTheSameValue(object columnType, object value)
    {
        var type = (BackupColumnType)columnType;

        var encoded = InDutchCulture(() => Encode(value, type));

        Assert.Equal(value, Decode(encoded, type));
    }

    [Fact]
    public void RoundTrip_Null_StaysNull()
    {
        Assert.Null(Decode(Encode(null, BackupColumnType.String), BackupColumnType.String));
    }

    /// <summary>Providers disagree about what a column hands back — SQLite returns a bool as a long, MySQL as an
    /// sbyte. The declared type, not the runtime one, decides the encoding.</summary>
    [Theory]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    public void Write_BooleanArrivingAsAnInteger_EncodesAsBoolean(long stored, bool expected)
    {
        Assert.Equal(expected, Decode(Encode(stored, BackupColumnType.Boolean), BackupColumnType.Boolean));
    }

    [Fact]
    public void Write_DateTimeOffsetArrivingAsADateTime_KeepsTheInstant()
    {
        var stored = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Unspecified);

        var restored = Decode(Encode(stored, BackupColumnType.DateTimeOffset), BackupColumnType.DateTimeOffset);

        Assert.Equal(new DateTimeOffset(stored, TimeSpan.Zero), restored);
    }

    [Fact]
    public void FromClrType_UnsupportedStoreType_IsRejectedRatherThanGuessed()
    {
        Assert.Null(BackupValueCodec.FromClrType(typeof(ulong)));
        Assert.Null(BackupValueCodec.FromClrType(typeof(BackupValueCodecTests)));
    }

    [Fact]
    public void FromClrType_NullableAndEnum_MapToTheirUnderlyingType()
    {
        Assert.Equal(BackupColumnType.Int32, BackupValueCodec.FromClrType(typeof(int?)));
        Assert.Equal(BackupColumnType.Int32, BackupValueCodec.FromClrType(typeof(DayOfWeek)));
    }

    private static string Encode(object? value, BackupColumnType type)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            BackupValueCodec.Write(writer, value, type, "Test.Column");
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static object? Decode(string json, BackupColumnType type)
    {
        using var document = JsonDocument.Parse(json);
        return BackupValueCodec.Read(document.RootElement[0], type, "Test.Column");
    }

    private static T InDutchCulture<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
