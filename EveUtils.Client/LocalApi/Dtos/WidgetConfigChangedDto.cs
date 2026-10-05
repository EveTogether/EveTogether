using EveUtils.Client.LocalApi.Widgets;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// The <c>widget.config</c> event: a saved widget was created, changed or deleted, so an open widget page applies the
/// new <see cref="Config"/> without a reload. <see cref="Config"/> is null on a delete.
/// </summary>
public sealed record WidgetConfigChangedDto(string Id, WidgetChangeKind Change, WidgetConfig? Config);
