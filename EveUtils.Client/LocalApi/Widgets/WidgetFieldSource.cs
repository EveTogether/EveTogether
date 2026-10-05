namespace EveUtils.Client.LocalApi.Widgets;

/// <summary>Where a widget field's data comes from today, shown beside the field in the manager.</summary>
public enum WidgetFieldSource
{
    /// <summary>Served by the Local API.</summary>
    Api,

    /// <summary>Known to the app, not served by the Local API yet: the widget cannot show it.</summary>
    App,

    /// <summary>Not collected anywhere yet.</summary>
    New
}
