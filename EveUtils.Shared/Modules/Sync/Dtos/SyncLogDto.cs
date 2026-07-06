namespace EveUtils.Shared.Modules.Sync.Dtos;

public record SyncLogDto(int Id, string EntityName, DateTimeOffset SyncedAtUtc, string? Note);
