using System.Threading.Tasks;

namespace EveUtils.Client.Fittings;

/// <summary>
/// The four fit export actions, shared across the Local tab, the fit-detail window and the fit-browser rows
/// . All backend already exists; this seam exists so the actions live in one place
/// instead of being duplicated per view-model. Each call takes a <see cref="FitExportRequest"/> carrying the fit
/// and the caller's view-model state.
/// </summary>
public interface IFitExportActions
{
    /// <summary>Push the fit to EVE (ESI /fittings) for a picked character.</summary>
    Task PushToEveAsync(FitExportRequest request);

    /// <summary>Share the fit to a coupled server, picking the server/character as needed.</summary>
    Task ShareToServerAsync(FitExportRequest request);

    /// <summary>Copy the fit's eveship.fit link straight to the clipboard — no window.</summary>
    Task CopyEveshipLinkAsync(FitExportRequest request);

    /// <summary>Open the EFT/DNA/URL export window.</summary>
    Task OpenEftWindowAsync(FitExportRequest request);
}
