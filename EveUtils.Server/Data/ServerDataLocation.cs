namespace EveUtils.Server.Data;

/// <summary>The resolved server data directory plus where it came from.</summary>
internal sealed record ServerDataLocation(string Path, ServerDataDirectorySource Source);
