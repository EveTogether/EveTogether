using System;

namespace EveUtils.Client.Gamelog;

/// <summary>A character's last known system by name, and the UTC moment it entered abyssal space while inside a run.</summary>
public sealed record CharacterLocation(string System, DateTime? AbyssalAnchor);
