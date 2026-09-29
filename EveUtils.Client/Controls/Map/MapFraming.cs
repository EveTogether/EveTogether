namespace EveUtils.Client.Controls.Map;

/// <summary>How a <see cref="MapFocusRequest"/> fits its systems into view.</summary>
public enum MapFraming
{
    /// <summary>One system is flown to at system zoom; several are framed with room around them, up to 16×.</summary>
    Systems,

    /// <summary>Follow fleet (ET-394): the members' 2D extent with a 40 px margin, never closer than 14× — all members in
    /// one system is 14×, and a wider spread never zooms further in.</summary>
    Fleet
}
