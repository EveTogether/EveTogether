namespace EveUtils.Shared.Modules.Sync.Entities;

/// <summary>Server-only EF entity (internal to the Sync module). Only in <c>ServerDbContext</c>.</summary>
public class SyncLog
{
    public int Id { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public DateTimeOffset SyncedAtUtc { get; set; }
    public string? Note { get; set; }
}
