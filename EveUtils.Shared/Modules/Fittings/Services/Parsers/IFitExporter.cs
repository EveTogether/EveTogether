using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Exports the internal <see cref="EsiFitting"/> model back to the community text formats (mirror of
/// <see cref="IFitTextImporter"/>): EFT (name-based, SDE-resolved) and the compact DNA string.
/// </summary>
public interface IFitExporter
{
    string ToEft(EsiFitting fit);

    string ToDna(EsiFitting fit);

    /// <summary>Builds a shareable eveship.fit URL (v3 format) for the fit.</summary>
    string ToEveshipUrl(EsiFitting fit);
}
