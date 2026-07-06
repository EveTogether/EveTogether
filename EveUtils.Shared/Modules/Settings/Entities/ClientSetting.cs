namespace EveUtils.Shared.Modules.Settings.Entities;

/// <summary>Client-only EF entity (internal to the Settings module). Only in <c>ClientDbContext</c>.</summary>
public class ClientSetting
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
