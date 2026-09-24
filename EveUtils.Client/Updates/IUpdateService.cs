using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.Updates;

public interface IUpdateService
{
    /// <summary>
    /// Asks the release feed whether a newer build exists. Success with a null value means this build is current;
    /// a failed check reports <see cref="MessageCodes.UpdateNotInstalled"/>, <see cref="MessageCodes.Timeout"/> or
    /// <see cref="MessageCodes.UpdateFailed"/> — never "up to date". <paramref name="channel"/> is read fresh on
    /// every call (ET-339), so switching it takes effect on the next check without a restart.
    /// </summary>
    Task<Result<AppRelease?>> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the build currently on offer and keeps it for <see cref="ApplyDownloadedUpdateAndRestart"/>.
    /// </summary>
    Task<Result> DownloadAsync(UpdateChannel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies what <see cref="DownloadAsync"/> fetched and restarts; a no-op when nothing was downloaded.
    /// </summary>
    void ApplyDownloadedUpdateAndRestart();
}
