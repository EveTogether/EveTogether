namespace EveUtils.Client.Runs;

/// <summary>Where an automatic publish of one group stands while it is not simply queued or done (ET-245).</summary>
public enum RunPublishPhase
{
    Publishing,
    Failed
}
