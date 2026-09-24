namespace EveUtils.Client.Updates;

// No silent channel drift (ET-339, mirrors Cockpit's AC-387): a channel nobody chose follows the running build,
// a channel somebody chose outlives a restart and beats the build.
public static class ChannelChoice
{
    // "Stable"/"Nightly" as stored (any casing); anything else — null, empty, or a name this build does not
    // know — reads as "nobody chose", not as an error.
    public static UpdateChannel? Stored(string? storedChannel) =>
        Enum.TryParse<UpdateChannel>(storedChannel, ignoreCase: true, out var channel) ? channel : null;

    // What channel is actually in force: the stored choice if there is one, otherwise whatever stream the
    // running build itself belongs to.
    public static UpdateChannel Resolve(string? storedChannel, string runningVersion) =>
        Stored(storedChannel) ?? BuildChannel.FromVersion(runningVersion);
}
