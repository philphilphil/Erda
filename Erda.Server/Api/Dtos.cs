namespace Erda.Server.Api;

// Small DTO records returned by / accepted by the JSON API. Deliberately decoupled from the EF
// entities so the persistence shape can change without breaking the wire contract, and so we never
// serialize heavy fields (e.g. prompt-version Content in list views).

/// <summary>A reminder or scheduled prompt as shown in the panel, with a computed next-fire string.</summary>
public sealed record ReminderDto(string Id, string Kind, string When, string Text, string Status, string NextFire);

/// <summary>The two reminder tables plus the count of rows whose <c>when</c> failed to parse.</summary>
public sealed record RemindersResponse(
    IReadOnlyList<ReminderDto> Reminders,
    IReadOnlyList<ReminderDto> ScheduledPrompts,
    int MalformedCount);

/// <summary>Request to create a reminder. <c>Kind</c> is "Reminder" or "Prompt".</summary>
public sealed record CreateReminderRequest(string? Kind, string? When, string? Text);

/// <summary>Active prompt content plus the version history (newest first, metadata only).</summary>
public sealed record PromptResponse(string ActiveContent, IReadOnlyList<PromptVersionDto> Versions);

/// <summary>One saved prompt version's metadata (no content).</summary>
public sealed record PromptVersionDto(int Id, DateTimeOffset CreatedAtUtc, bool IsActive, string? Note);

/// <summary>Request to save a new prompt version.</summary>
public sealed record SavePromptRequest(string? Content, string? Note);

/// <summary>One activity-feed entry for display.</summary>
public sealed record ActivityDto(long Id, DateTimeOffset TimestampUtc, string Kind, string Summary);

/// <summary>
/// One editable config knob: its key, label/hint, the value to prefill the input with
/// (<c>Value</c> = pending override if any, else <c>Effective</c>), the currently-running
/// <c>Effective</c> value, and whether a saved override exists (pending a restart).
/// </summary>
public sealed record ConfigItemDto(string Key, string Label, string Hint, string? Value, string? Effective, bool Overridden);

/// <summary>Request to set/clear config overrides. A null/blank value clears that key's override.</summary>
public sealed record ConfigUpdateRequest(IReadOnlyDictionary<string, string?>? Values);

/// <summary>Login credentials. <c>Username</c> is optional (only checked when configured).</summary>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>Whether the panel requires auth, and whether the caller is currently authenticated.</summary>
public sealed record AuthState(bool AuthRequired, bool Authenticated);

/// <summary>A simple error payload: <c>{ "error": "…" }</c>.</summary>
public sealed record ErrorResponse(string Error);
