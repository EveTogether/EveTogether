namespace EveUtils.Client.Implants;

/// <summary>The result of importing a character's implants: status, how many were stored, and a message.</summary>
public sealed record ImplantImportResult(ImplantImportStatus Status, int ImplantCount, string? Message = null)
{
    public bool IsSuccess => Status == ImplantImportStatus.Imported;

    public static ImplantImportResult Ok(int count) => new(ImplantImportStatus.Imported, count);
}
