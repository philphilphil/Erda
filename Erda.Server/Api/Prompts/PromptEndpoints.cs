using Erda.Core.Data;

namespace Erda.Server.Api;

/// <summary>
/// JSON endpoints over <see cref="IPromptStore"/> for the panel's System Prompt screen. Saving a new
/// version or activating an old one only takes effect after a restart (the agent reads the active
/// version once at startup) — matching v1's restart-to-apply behavior.
/// </summary>
public static class PromptEndpoints
{
    /// <summary>Sanity ceiling so a runaway paste can't write a multi-megabyte prompt row.</summary>
    private const int MaxPromptChars = 100_000;

    public static RouteGroupBuilder MapPromptEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/prompt");

        g.MapGet("", (IPromptStore prompts) =>
        {
            var versions = prompts.ListVersions();
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
            var row = prompts.SaveNewVersion(content, note);
            return Results.Ok(new PromptVersionDto(row.Id, row.CreatedAtUtc, row.IsActive, row.Note));
        });

        g.MapPost("/versions/{id:int}/activate", (int id, IPromptStore prompts) =>
            prompts.Activate(id) ? Results.Ok() : Results.NotFound());

        return group;
    }
}
