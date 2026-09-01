using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveUtils.Server.Backup;

/// <summary>Serializer settings shared by the manifest and the per-table headers. Enums travel as their names, so
/// the archive stays readable and reordering the <see cref="BackupColumnType"/> members can never change how an
/// existing archive is interpreted.</summary>
internal static class BackupJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };
}
