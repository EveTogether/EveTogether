using System;
using EveUtils.Shared.Modules.Gamelog.Reading;

namespace EveUtils.Client.Gamelog;

/// <summary>Where the GAME LOGS screen (ET-410) gets the lines of the gamelog folder: the buffer that sits on the one
/// watcher that already tails it.</summary>
public interface IGameLogLineSource
{
    /// <summary>The buffer on the watcher that runs now; null until the watcher has started.</summary>
    GameLogLineBuffer? CurrentBuffer { get; }

    /// <summary>Raised when the watcher was restarted (another folder): a screen must read again from
    /// <see cref="CurrentBuffer"/>, because lines written in between were baselined away.</summary>
    event Action? BufferReplaced;
}
