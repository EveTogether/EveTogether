using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Parses a pasted community fit (EFT or DNA, auto-detected) into the internal <see cref="EsiFitting"/> model,
/// resolving names↔typeIds and slot flags via the SDE. Pure and network-free; fetching a fit behind a link and
/// storing the result are the command handler's job.
/// </summary>
public interface IFitTextImporter
{
    FitTextFormat Detect(string text);

    FitImportResult Import(string text);
}
