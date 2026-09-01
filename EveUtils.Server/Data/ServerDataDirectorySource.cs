namespace EveUtils.Server.Data;

/// <summary>Where the resolved server data directory came from — see <see cref="ServerDataDirectory"/>.</summary>
internal enum ServerDataDirectorySource
{
    /// <summary>The built-in per-user default, because neither override was set.</summary>
    Default,

    /// <summary>The <c>EVEUTILS_SERVER_DATA_DIR</c> environment variable (Docker, headless test isolation).</summary>
    EnvironmentVariable,

    /// <summary>The <c>Server:DataDirectory</c> configuration key.</summary>
    Configuration
}
