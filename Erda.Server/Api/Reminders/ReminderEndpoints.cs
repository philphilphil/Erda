using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Microsoft.Extensions.Options;

namespace Erda.Server.Api;

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

            var reminders = load.Reminders.Where(r => r.Kind == ReminderKind.Reminder).Select(r => Map(r, now, zone)).ToList();
            var prompts = load.Reminders.Where(r => r.Kind == ReminderKind.Prompt).Select(r => Map(r, now, zone)).ToList();
            return Results.Ok(new RemindersResponse(reminders, prompts, load.Malformed.Count));
        });

        g.MapPost("", (CreateReminderRequest req, ReminderStore store, IClock clock, IOptions<ReminderOptions> opts) =>
            CreateReminder(req, store, clock, opts));

        g.MapPut("/{id}", (string id, UpdateReminderRequest req, ReminderStore store, IClock clock, IOptions<ReminderOptions> opts) =>
            UpdateReminder(id, req, store, clock, opts));

        g.MapPost("/{id}/pause", (string id, ReminderStore store) =>
            store.SetStatus(id, ReminderStatus.Paused) ? Results.Ok() : Results.NotFound());

        g.MapPost("/{id}/resume", (string id, ReminderStore store) =>
            store.SetStatus(id, ReminderStatus.Active) ? Results.Ok() : Results.NotFound());

        g.MapDelete("/{id}", (string id, ReminderStore store) =>
            store.Remove(id) ? Results.Ok() : Results.NotFound());

        return group;
    }

    /// <summary>Map a reminder to its wire DTO, computing the next-fire string in <paramref name="zone"/>.</summary>
    internal static ReminderDto Map(Reminder r, DateTimeOffset now, TimeZoneInfo zone) => new(
        r.Id, r.Kind.ToString(), r.When, r.Text, r.Status.ToString(),
        ReminderView.NextFire(r.Spec, now, zone), r.DirectToCodex, r.PreScript);

    /// <summary>Create a reminder or scheduled prompt. Codex-direct/pre-script apply only to prompts.</summary>
    internal static IResult CreateReminder(
        CreateReminderRequest req, ReminderStore store, IClock clock, IOptions<ReminderOptions> opts)
    {
        var text = req.Text?.Trim() ?? "";
        var when = req.When?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(text))
            return TypedResults.BadRequest(new ErrorResponse("Text is required."));
        if (!Enum.TryParse<ReminderKind>(req.Kind, ignoreCase: true, out var kind))
            return TypedResults.BadRequest(new ErrorResponse("Kind must be 'Reminder' or 'Prompt'."));
        if (!WhenSpec.TryParse(when, out var spec))
            return TypedResults.BadRequest(new ErrorResponse("Couldn't parse that schedule."));

        // Codex-direct and pre-scripts are prompt-only; blank them for verbatim reminders.
        var directToCodex = kind == ReminderKind.Prompt && (req.DirectToCodex ?? false);
        var preScript = kind == ReminderKind.Prompt ? Blank(req.PreScript) : null;

        var existing = store.LoadAll().Reminders.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = ReminderView.UniqueId(ReminderView.Slugify(text), existing);
        store.Append(kind, id, when, text, directToCodex, preScript);

        var zone = ReminderView.ResolveZone(opts.Value.TimeZone);
        var dto = new ReminderDto(id, kind.ToString(), when, text, ReminderStatus.Active.ToString(),
            ReminderView.NextFire(spec!, clock.UtcNow, zone), directToCodex, preScript);
        return TypedResults.Ok(dto);
    }

    /// <summary>Edit a scheduled prompt's definition in place, preserving id/kind/status/run-state.</summary>
    internal static IResult UpdateReminder(
        string id, UpdateReminderRequest req, ReminderStore store, IClock clock, IOptions<ReminderOptions> opts)
    {
        var text = req.Text?.Trim() ?? "";
        var when = req.When?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(text))
            return TypedResults.BadRequest(new ErrorResponse("Text is required."));
        if (!WhenSpec.TryParse(when, out var spec))
            return TypedResults.BadRequest(new ErrorResponse("Couldn't parse that schedule."));

        if (!store.Update(id, when, text, req.DirectToCodex ?? false, Blank(req.PreScript)))
            return TypedResults.NotFound();

        var zone = ReminderView.ResolveZone(opts.Value.TimeZone);
        var row = store.LoadAll().Reminders.FirstOrDefault(r => r.Id == id);
        // Round-trip the saved row so the response reflects the preserved status/kind, not assumptions.
        var dto = row is not null
            ? Map(row, clock.UtcNow, zone)
            : new ReminderDto(id, ReminderKind.Prompt.ToString(), when, text, ReminderStatus.Active.ToString(),
                ReminderView.NextFire(spec!, clock.UtcNow, zone), req.DirectToCodex ?? false, Blank(req.PreScript));
        return TypedResults.Ok(dto);
    }

    /// <summary>Trim a string and collapse blank/whitespace to null (so "no script" is stored as null).</summary>
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
