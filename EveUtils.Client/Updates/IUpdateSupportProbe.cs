namespace EveUtils.Client.Updates;

public interface IUpdateSupportProbe
{
    /// <summary>
    /// Whether this copy was placed by the installer and can therefore replace itself.
    /// </summary>
    UpdateSupport Detect();
}
