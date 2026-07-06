namespace EveUtils.Shared.Modules.Fittings.Dtos;

/// <summary>
/// Outcome of parsing a pasted fit. On success carries the assembled <see cref="EsiFitting"/> (internal
/// model, ready to store) plus any non-fatal <see cref="Warnings"/> (e.g. a module name the SDE didn't recognise,
/// skipped). On failure <see cref="Error"/> explains why (bad format, unknown ship, SDE not loaded).
/// </summary>
public sealed record FitImportResult(EsiFitting? Fit, IReadOnlyList<string> Warnings, string? Error)
{
    public bool Success => Error is null && Fit is not null;

    public static FitImportResult Ok(EsiFitting fit, IReadOnlyList<string> warnings) => new(fit, warnings, null);
    public static FitImportResult Failed(string error) => new(null, [], error);
}
