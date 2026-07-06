namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>Lifecycle phase of an SDE import, surfaced through <see cref="SdeImportProgress"/>.</summary>
public enum SdeImportPhase
{
    CheckingVersion,
    Downloading,
    Preparing,
    Processing,
    Finalizing,
    Completed,
    AlreadyUpToDate,
    Failed
}
