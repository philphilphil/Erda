using Erda.Core.Data;
using Erda.Agents.Workflows;

namespace Erda.Server.Api;

/// <summary>
/// JSON endpoints over <see cref="IPromptStore"/> for the panel's Prompts screen. Two prompts share
/// the store: the orchestrator's <b>system prompt</b> (versioned, with rollback) and the
/// <b>voice-memo prompt</b> (edited save-in-place, no history). The system prompt applies on the next
/// restart (the agent reads it once at startup); the voice-memo prompt is read per memo, so its edits
/// apply to the next memo processed.
/// </summary>
public static class PromptEndpoints
{
    /// <summary>Sanity ceiling so a runaway paste can't write a multi-megabyte prompt row.</summary>
    private const int MaxPromptChars = 100_000;

    public static RouteGroupBuilder MapPromptEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/prompt");

        // --- System prompt (versioned) ---
        g.MapGet("", (IPromptStore prompts) =>
        {
            var versions = prompts.ListVersions(PromptKind.System);
            var active = versions.FirstOrDefault(v => v.IsActive);
            var dtos = versions
                .Select(v => new PromptVersionDto(v.Id, v.CreatedAtUtc, v.IsActive, v.Note))
                .ToList();
            return Results.Ok(new PromptResponse(active?.Content ?? "", dtos));
        });

        g.MapPost("", (SavePromptRequest req, IPromptStore prompts) =>
        {
            var content = req.Content ?? "";
            if (string.IsNullOrWhiteSpace(content))
                return Results.BadRequest(new ErrorResponse("Prompt content is required."));
            if (content.Length > MaxPromptChars)
                return Results.BadRequest(new ErrorResponse($"Prompt is too long (max {MaxPromptChars} chars)."));

            var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            var row = prompts.SaveNewVersion(PromptKind.System, content, note);
            return Results.Ok(new PromptVersionDto(row.Id, row.CreatedAtUtc, row.IsActive, row.Note));
        });

        g.MapPost("/versions/{id:int}/activate", (int id, IPromptStore prompts) =>
            prompts.Activate(id) ? Results.Ok() : Results.NotFound());

        // --- Voice-memo prompt (save-in-place; the code default seeds first read) ---
        g.MapGet("/voice", (IPromptStore prompts) =>
        {
            var content = prompts.GetActiveContent(PromptKind.Voice, VoiceMemoWorkflow.DeveloperInstruction);
            return Results.Ok(new VoicePromptResponse(content));
        });

        g.MapPut("/voice", (SaveVoicePromptRequest req, IPromptStore prompts) =>
        {
            var content = req.Content ?? "";
            if (string.IsNullOrWhiteSpace(content))
                return Results.BadRequest(new ErrorResponse("Voice-memo prompt content is required."));
            if (content.Length > MaxPromptChars)
                return Results.BadRequest(new ErrorResponse($"Prompt is too long (max {MaxPromptChars} chars)."));

            prompts.SaveNewVersion(PromptKind.Voice, content, null);
            return Results.Ok();
        });

        return group;
    }
}
