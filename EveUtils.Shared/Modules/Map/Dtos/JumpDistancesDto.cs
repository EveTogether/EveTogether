namespace EveUtils.Shared.Modules.Map.Dtos;

/// <summary>How many jumps every system of the map is from one origin system (ET-399).</summary>
public sealed class JumpDistancesDto
{
    internal const int Unreachable = -1;

    private readonly int[] _jumps;

    internal JumpDistancesDto(int fromIndex, int[] jumps)
    {
        FromIndex = fromIndex;
        _jumps = jumps;
    }

    /// <summary>Index into <see cref="MapGraphDto.Systems"/> of the system the jumps are counted from.</summary>
    public int FromIndex { get; }

    /// <summary>The jumps from the origin to the system at <paramref name="systemIndex"/>; null when no route leads there
    /// (Pochven, the Jove regions) or the index is not on the map.</summary>
    public int? JumpsTo(int systemIndex) =>
        systemIndex >= 0 && systemIndex < _jumps.Length && _jumps[systemIndex] != Unreachable ? _jumps[systemIndex] : null;
}
