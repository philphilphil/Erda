namespace Erda.Server.Api;

// Wire-contract DTOs for the reminders endpoints. Deliberately decoupled from the EF entities so
// the persistence shape can change without breaking the API.

/// <summary>A reminder or scheduled prompt as shown in the panel, with a computed next-fire string.
/// <c>PreScript</c> is meaningful only for scheduled prompts.</summary>
public sealed record ReminderDto(
    string Id, string Kind, string When, string Text, string Status, string NextFire,
    string? PreScript);

/// <summary>The two reminder tables plus the count of rows whose <c>when</c> failed to parse.</summary>
public sealed record RemindersResponse(
    IReadOnlyList<ReminderDto> Reminders,
    IReadOnlyList<ReminderDto> ScheduledPrompts,
    int MalformedCount);

/// <summary>Request to create a reminder. <c>Kind</c> is "Reminder" or "Prompt". The
/// <c>PreScript</c> field is applied only when the kind is "Prompt".</summary>
public sealed record CreateReminderRequest(
    string? Kind, string? When, string? Text, string? PreScript = null);

/// <summary>Request to edit a scheduled prompt in place (id, kind, status and run-state preserved).</summary>
public sealed record UpdateReminderRequest(
    string? When, string? Text, string? PreScript = null);
