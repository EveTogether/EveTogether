namespace EveUtils.Client.Updates;

/// <summary>Whether this copy can replace itself — the same build, run two ways, answers differently.</summary>
public enum UpdateSupport
{
    /// <summary>Placed by the installer, so downloading and applying an update is something that can be offered.</summary>
    Supported,

    /// <summary>A checkout, an unpacked zip/tarball or a test host: an ordinary state, not a fault. It can be told
    /// a newer build exists, but whoever put it here is the one who replaces it.</summary>
    NotInstalled,
}
