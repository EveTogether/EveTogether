using System.Linq;
using System.Threading;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.LocalApi.Widgets;
using EveUtils.Shared.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EveUtils.Client.LocalApi;

/// <summary>The widget page <c>/w/&lt;id&gt;</c> and the manager's endpoints under <c>/api/v1/widgets</c>.</summary>
internal static class LocalApiWidgetEndpoints
{
    public static void MapWidgetEndpoints(this WebApplication app, string baseUrl, string? apiKey)
    {
        // Unknown id: an empty 404, so a stale OBS source shows nothing rather than an error on stream.
        app.MapGet("/w/{id}", async (string id, WidgetStore widgets, CancellationToken ct) =>
                await widgets.GetAsync(id, ct) is null
                    ? Results.NotFound()
                    : Results.Content(LocalApiDocs.Render(LocalApiDocs.WidgetPageResource, baseUrl), "text/html"))
            .ExcludeFromDescription();

        var group = app.MapGroup("/api/v1/widgets").WithTags("Widgets");

        group.MapGet("/", async (WidgetStore widgets, CancellationToken ct) =>
                (await widgets.ListAsync(ct)).Select(widget => WidgetDto.FromConfig(widget, baseUrl, apiKey)).ToList())
            .WithSummary("List the built-in presets and the saved widgets")
            .WithDescription("Built-in presets come first (isBuiltIn = true, id = the preset key, e.g. live-dps), then the "
                + "saved widgets. url is what OBS loads; it carries ?key= when an API key is set.");

        group.MapGet("/{id}", async (string id, WidgetStore widgets, CancellationToken ct) =>
                await widgets.GetAsync(id, ct) is { } widget
                    ? Results.Ok(WidgetDto.FromConfig(widget, baseUrl, apiKey))
                    : Results.NotFound())
            .WithSummary("One widget or preset")
            .Produces<WidgetDto>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", async (WidgetConfig config, WidgetStore widgets, CancellationToken ct) =>
                await widgets.CreateAsync(config, ct) is var result && result is { IsSuccess: true, Value: { } created }
                    ? Results.Created($"/api/v1/widgets/{created.Id}", WidgetDto.FromConfig(created, baseUrl, apiKey))
                    : _Refused(result))
            .WithSummary("Save a widget as a new copy")
            .WithDescription("Customizing a preset: GET the preset, change its config and POST it here. The widget is "
                + "always saved under a new id; the id in the body is ignored.")
            .Produces<WidgetDto>(StatusCodes.Status201Created)
            .Produces<LocalApiErrorDto>(StatusCodes.Status400BadRequest);

        group.MapPut("/{id}", async (string id, WidgetConfig config, WidgetStore widgets, CancellationToken ct) =>
                await widgets.UpdateAsync(config with { Id = id }, ct) is var result && result is { IsSuccess: true, Value: { } updated }
                    ? Results.Ok(WidgetDto.FromConfig(updated, baseUrl, apiKey))
                    : _Refused(result))
            .WithSummary("Change a saved widget")
            .WithDescription("An open widget page receives the new config over /ws as widget.config and applies it "
                + "without a reload. Built-in presets are read-only (409 PRESET_READ_ONLY).")
            .Produces<WidgetDto>()
            .Produces<LocalApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<LocalApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<LocalApiErrorDto>(StatusCodes.Status409Conflict);

        group.MapDelete("/{id}", async (string id, WidgetStore widgets, CancellationToken ct) =>
                await widgets.DeleteAsync(id, ct) is var result && result.IsSuccess ? Results.NoContent() : _Refused(result))
            .WithSummary("Delete a saved widget")
            .WithDescription("Built-in presets cannot be deleted (409 PRESET_READ_ONLY).")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<LocalApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<LocalApiErrorDto>(StatusCodes.Status409Conflict);
    }

    private static IResult _Refused(Result result)
    {
        var message = result.Messages.FirstOrDefault()
            ?? new ResultMessage(MessageSeverity.Error, MessageCodes.ServerError, "The widget could not be saved.");
        var status = message.Code switch
        {
            MessageCodes.NotFound => StatusCodes.Status404NotFound,
            MessageCodes.PresetReadOnly => StatusCodes.Status409Conflict,
            MessageCodes.ValidationFailed => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };
        return Results.Json(new LocalApiErrorDto(message.Code, message.Text), statusCode: status);
    }
}
