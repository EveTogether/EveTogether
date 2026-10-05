using System;

namespace EveUtils.Client.Pairing;

/// <summary>A server that did not answer within the contact timeout: down, blocked, or the wrong address.</summary>
public sealed class ServerUnreachableException(string serverAddress, TimeSpan timeout, Exception inner)
    : Exception($"No answer from {serverAddress} within {timeout.TotalSeconds:0} seconds.", inner)
{
    public string ServerAddress { get; } = serverAddress;
    public TimeSpan Timeout { get; } = timeout;
}
