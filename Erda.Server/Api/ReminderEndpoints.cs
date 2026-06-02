using Erda.Configuration;
using Erda.Scheduling;
using Erda.Services;
using Microsoft.Extensions.Options;

namespace Erda.Api;

/// <summary>
/// JSON endpoints over <see cref="ReminderStore"/> for the panel's Reminders screen. The scheduler
/// reads the same rows each tick, so edits here take effect on the next poll — no restart needed.
/// </summary>
public static class ReminderEndpoints
{
    public static RouteGroupBuilder MapReminderEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/reminders");

        g.MapGet("", (ReminderStore store, IClock clock, IOptions<ReminderOptions> opts) =>
        {
            var zone = ReminderView.ResolveZone(opts.Value.TimeZone);
            var now = clock.UtcNow;
            var load = store.LoadAll();

            ReminderDto Map(Reminder r) => new(
                r.Id, r.Kind.ToString(), r.When, r.Text, r.Status.ToString(),
                ReminderView.NextFire(r.Spec, now, zone));

            var reminders = load.Reminders.Where(r => r.Kind == ReminderKind.Reminder).Select(Map).ToList();
            var prompts = load.Reminders.Where(r => r.Kind == ReminderKind.Prompt).Select(Map).ToList();
            return Results.Ok(new RemindersResponse(reminders, prompts, load.Malformed.Count));
        });

        g.MapPost("", (CreateReminderRequest req, ReminderStore store, IClock clock, IOptions<ReminderOptions> opts) =>
        {
            var text = req.Text?.Trim() ?? "";
            var when = req.When?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new ErrorResponse("Text is required."));
            if (!Enum.TryParse<ReminderKind>(req.Kind, ignoreCase: true, out var kind))
                return Results.BadRequest(new ErrorResponse("Kind must be 'Reminder' or 'Prompt'."));
            if (!WhenSpec.TryParse(when, out var spec))
                return Results.BadRequest(new ErrorResponse("Couldn't parse that schedule."));

            var existing = store.LoadAll().Reminders
                .Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var id = ReminderView.UniqueId(ReminderView.Slugify(text), existing);
            store.Append(kind, id, when, text);

            var zone = ReminderView.ResolveZone(opts.Value.TimeZone);
            var dto = new ReminderDto(id, kind.ToString(), when, text, ReminderStatus.Active.ToString(),
                ReminderView.NextFire(spec!, clock.UtcNow, zone));
            return Results.Ok(dto);
        });

        g.MapPost("/{id}/pause", (string id, ReminderStore store) =>
            store.SetStatus(id, ReminderStatus.Paused) ? Results.Ok() : Results.NotFound());

        g.MapPost("/{id}/resume", (string id, ReminderStore store) =>
            store.SetStatus(id, ReminderStatus.Active) ? Results.Ok() : Results.NotFound());

        g.MapDelete("/{id}", (string id, ReminderStore store) =>
            store.Remove(id) ? Results.Ok() : Results.NotFound());

        return group;
    }
}
