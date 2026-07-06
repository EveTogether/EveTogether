using System;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.UiTests;

/// <summary>An <see cref="IEsiOutageDetector"/> that counts the outcomes fed to it, for asserting what a caller reports.</summary>
public sealed class RecordingEsiOutageDetector : IEsiOutageDetector
{
    public int Successes { get; private set; }
    public int ServerFailures { get; private set; }
    public int Resets { get; private set; }
    public bool IsSuspect { get; set; }

    public void RecordSuccess() => Successes++;
    public void RecordServerFailure() => ServerFailures++;
    public void Reset() => Resets++;

    public event Action OutageSuspected { add { } remove { } }
}
