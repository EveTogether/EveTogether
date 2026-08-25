using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Updates;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="IUpdateService"/> double for headless UI tests: answers with whatever the test set, so the update UI
/// can be driven without a feed, a network or an installed copy.
/// </summary>
public sealed class FakeUpdateService : IUpdateService
{
    /// <summary>What a check answers. Default: the feed answered and this build is current.</summary>
    public Func<Task<Result<AppRelease?>>> OnCheck { get; set; } =
        () => Task.FromResult(Result<AppRelease?>.Success(null));

    /// <summary>What a download answers. Default: it worked.</summary>
    public Func<Task<Result>> OnDownload { get; set; } = () => Task.FromResult(Result.Success());

    /// <summary>How often a check was asked for — so a test can assert one was skipped entirely.</summary>
    public int Checks { get; private set; }

    /// <summary>How often the downloaded package was applied.</summary>
    public int Applied { get; private set; }

    public Task<Result<AppRelease?>> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        return OnCheck();
    }

    public Task<Result> DownloadAsync(CancellationToken cancellationToken = default) => OnDownload();

    public void ApplyDownloadedUpdateAndRestart() => Applied++;
}
