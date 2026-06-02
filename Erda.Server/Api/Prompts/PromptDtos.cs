namespace Erda.Server.Api;

/// <summary>Active prompt content plus the version history (newest first, metadata only).</summary>
public sealed record PromptResponse(string ActiveContent, IReadOnlyList<PromptVersionDto> Versions);

/// <summary>One saved prompt version's metadata (no content).</summary>
public sealed record PromptVersionDto(int Id, DateTimeOffset CreatedAtUtc, bool IsActive, string? Note);

/// <summary>Request to save a new prompt version.</summary>
public sealed record SavePromptRequest(string? Content, string? Note);

/// <summary>The voice-memo prompt's active content (no version history — edited save-in-place).</summary>
public sealed record VoicePromptResponse(string Content);

/// <summary>Request to overwrite the voice-memo prompt.</summary>
public sealed record SaveVoicePromptRequest(string? Content);
