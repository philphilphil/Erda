using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Erda.Core.WhatsApp;
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

        // Run a scheduled prompt right now, out of band. ApplicationStopping (not RequestAborted) is the
        // dispatch token so the fire-and-forget run survives the 202 response and stops only on shutdown.
        g.MapPost("/{id}/run", (string id, ReminderStore store, ReminderDispatcher dispatcher,
            IOptions<WhatsAppOptions> whatsApp, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) =>
            RunNow(id, store, dispatcher, WhatsAppJid.FromNumber(whatsApp.Value.OwnerNumber),
                lifetime.ApplicationStopping, loggerFactory.CreateLogger("Erda.Reminders.RunNow")));

        g.MapDelete("/{id}", (string id, ReminderStore store) =>
            store.Remove(id) ? Results.Ok() : Results.NotFound());

        return group;
    }

    /// <summary>
    /// Kick off a scheduled prompt out of band (prompt-only; 400 for a verbatim reminder). Fire-and-forget:
    /// the agent run can take many seconds, so we don't block — the reply lands on WhatsApp like a normal
    /// fire. Deliberately touches neither run-state nor status, so the schedule is unaffected.
    /// </summary>
    internal static IResult RunNow(string id, ReminderStore store, ReminderDispatcher dispatcher,
        string ownerJid, CancellationToken ct, ILogger logger)
    {
        var row = store.LoadAll().Reminders.FirstOrDefault(r => r.Id == id);
        if (row is null)
            return TypedResults.NotFound();
        if (row.Kind != ReminderKind.Prompt)
            return TypedResults.BadRequest(new ErrorResponse("Only scheduled prompts can be run on demand."));

        _ = Task.Run(async () =>
        {
            try { await dispatcher.DispatchAsync(row, ownerJid, manual: true, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* host shutdown — expected */ }
            catch (Exception ex) { logger.LogError(ex, "Manual run of prompt '{Id}' failed.", id); }
        });
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>Map a reminder to its wire DTO, computing the next-fire string in <paramref name="zone"/>.</summary>
    internal static ReminderDto Map(Reminder r, DateTimeOffset now, TimeZoneInfo zone) => new(
        r.Id, r.Kind.ToString(), r.When, r.Text, r.Status.ToString(),
        ReminderView.NextFire(r.Spec, now, zone), r.PreScript);

    /// <summary>Create a reminder or scheduled prompt. The pre-script applies only to prompts.</summary>
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

        // Pre-scripts are prompt-only; blank them for verbatim reminders.
        var preScript = kind == ReminderKind.Prompt ? Blank(req.PreScript) : null;

        var existing = store.LoadAll().Reminders.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = ReminderView.UniqueId(ReminderView.Slugify(text), existing);
        store.Append(kind, id, when, text, preScript);

        var zone = ReminderView.ResolveZone(opts.Value.TimeZone);
        var dto = new ReminderDto(id, kind.ToString(), when, text, ReminderStatus.Active.ToString(),
            ReminderView.NextFire(spec!, clock.UtcNow, zone), preScript);
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

        if (!store.Update(id, when, text, Blank(req.PreScript)))
            return TypedResults.NotFound();

        var zone = ReminderView.ResolveZone(opts.Value.TimeZone);
        var row = store.LoadAll().Reminders.FirstOrDefault(r => r.Id == id);
        // Round-trip the saved row so the response reflects the preserved status/kind, not assumptions.
        var dto = row is not null
            ? Map(row, clock.UtcNow, zone)
            : new ReminderDto(id, ReminderKind.Prompt.ToString(), when, text, ReminderStatus.Active.ToString(),
                ReminderView.NextFire(spec!, clock.UtcNow, zone), Blank(req.PreScript));
        return TypedResults.Ok(dto);
    }

    /// <summary>Trim a string and collapse blank/whitespace to null (so "no script" is stored as null).</summary>
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
