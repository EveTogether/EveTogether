namespace EveUtils.Client.Skills;

/// <summary>The result of importing a character's skills: status, how many skills were stored, and a message.</summary>
public sealed record SkillImportResult(SkillImportStatus Status, int SkillCount, string? Message = null)
{
    public bool IsSuccess => Status == SkillImportStatus.Imported;

    public static SkillImportResult Ok(int count) => new(SkillImportStatus.Imported, count);
}
