namespace EveUtils.Server.Backup;

/// <summary>
/// The closed set of column types a backup archive can carry. Deliberately closed: a store type that is not on
/// this list fails the export by name rather than being written out as whatever <c>ToString</c> happened to
/// produce and coming back subtly different — a wrong value in <c>SyncedCharacter</c> is not recoverable.
///
/// The name of each member is written into the archive, so these names are part of the format and are not
/// renamed. New members may be appended.
/// </summary>
internal enum BackupColumnType
{
    Boolean,
    Byte,
    Int16,
    Int32,
    Int64,
    Decimal,
    Double,
    Single,
    String,
    Guid,
    DateTime,
    DateTimeOffset,
    TimeSpan,
    Bytes,
}
