using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Implants;

/// <summary>Imports a character's plugged-in implants from ESI and caches their type ids.</summary>
public interface IEsiImplantImporter
{
    Task<ImplantImportResult> ImportAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Raised after a successful import with the character's resolved implant type ids, so the overview
    /// badge can refresh live instead of only on the next character-list rebuild (re-auth/restart).</summary>
    event Action<int, IReadOnlyList<int>> ImplantsChanged;
}
