namespace EveUtils.Client.Controls;

/// <summary>Which of the <see cref="DpsGraph"/>'s stacked lanes a line is drawn in — one lane per unit.</summary>
public enum GraphLane
{
    /// <summary>Damage and repair, hp/s.</summary>
    HitPoints,

    /// <summary>Energy neutralized and capacitor transferred, GJ/s.</summary>
    Capacitor,
}
