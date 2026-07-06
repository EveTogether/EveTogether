using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>Outcome of an import attempt.</summary>
public sealed record SdeImportResult(bool Updated, SdeVersion? Version, string? Error = null)
{
    public bool Success => Error is null;

    public static SdeImportResult UpToDate(SdeVersion version) => new(Updated: false, version);
    public static SdeImportResult Imported(SdeVersion version) => new(Updated: true, version);
    public static SdeImportResult Failed(string error) => new(Updated: false, Version: null, error);
}
