using System;
using EveUtils.Shared.Modules.Gamelog.Aggregation;

namespace EveUtils.Client.Esi;

/// <summary>
/// One reading from the ESI location watch: where a character was, and when we looked. Two readers take different
/// halves of it — the abyssal countdown wants <see cref="Inside"/> (ET-62), the location bootstrap wants the
/// <see cref="SolarSystemId"/> itself (ET-63) — so the reading travels whole rather than as one reader's verdict.
/// </summary>
/// <param name="SolarSystemId">The system ESI placed the character in, or null when the watch could read nothing.</param>
/// <param name="AtUtc">When the reading was taken. The next abyssal run anchors on this, so it is the moment of the
/// poll and not the moment a reader gets around to it.</param>
public readonly record struct EsiLocationReading(int? SolarSystemId, DateTime AtUtc)
{
    /// <summary>The watch can report nothing further: no scope, no working token, or unbroken failure.</summary>
    public static EsiLocationReading Lost(DateTime atUtc) => new(null, atUtc);

    /// <summary>
    /// <c>true</c> = inside abyssal deadspace, <c>false</c> = outside, <c>null</c> = the watch was lost. Derived
    /// here so the gamelog side still does not have to know how abyssal space is recognised.
    /// </summary>
    public bool? Inside => SolarSystemId is { } id ? AbyssalSpace.IsAbyssalSystem(id) : null;

    /// <summary>A reading that places the character in an ordinary, nameable solar system.</summary>
    public bool IsOutside => Inside is false;
}
