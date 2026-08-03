using Erda.Core.Services;

namespace Erda.Tests;

/// <summary>
/// A recording <see cref="IAppleBridgeClient"/> for the agent-tool tests. Shared between
/// <see cref="AppleReminderToolsTests"/> and <see cref="AppleCalendarToolsTests"/>: both tool
/// classes talk to the same client, and two copies of this would drift the moment the interface
/// gained a method — which is exactly what happened when it gained the calendar half.
/// </summary>
internal sealed class FakeAppleBridgeClient : IAppleBridgeClient
{
    public AppleBridgeResult<AppleBridgeStatus> StatusResult { get; set; } =
        AppleBridgeResult<AppleBridgeStatus>.Ok(new AppleBridgeStatus("ok", [], "ok", []));
    public AppleBridgeResult<AppleReminder> CreateResult { get; set; } =
        AppleBridgeResult<AppleReminder>.Fail("not configured for this test");
    public AppleBridgeResult<IReadOnlyList<AppleReminder>> ListResult { get; set; } =
        AppleBridgeResult<IReadOnlyList<AppleReminder>>.Ok(Array.Empty<AppleReminder>());
    public AppleBridgeResult<AppleReminderCompletion> CompleteResult { get; set; } =
        AppleBridgeResult<AppleReminderCompletion>.Fail("not configured for this test");
    public AppleBridgeResult<AppleCalendarEvent> CreateEventResult { get; set; } =
        AppleBridgeResult<AppleCalendarEvent>.Fail("not configured for this test");
    public AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>> ListEventsResult { get; set; } =
        AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok(Array.Empty<AppleCalendarEvent>());

    // No list in here, and that is the point: the create route takes none — the write target is
    // pinned on the Mac. A test asserting "the right list was passed" is a test that cannot exist
    // any more, exactly as for the calendar create below.
    public (string Title, string? Notes, DateTimeOffset? DueAt, int? Priority)? CreateCall { get; private set; }
    public (IReadOnlyList<string>? Lists, int? Limit)? ListCall { get; private set; }
    public string? CompleteCall { get; private set; }
    // No calendar in here, and that is the point: the create route takes none — the write target is
    // pinned on the Mac. A test asserting "the right calendar was passed" is a test that cannot
    // exist any more.
    public (string Title, DateTimeOffset StartAt, DateTimeOffset EndAt, string? Notes, string? TimeZone)? CreateEventCall { get; private set; }
    public (IReadOnlyList<string>? Calendars, int? Days, int? Limit)? ListEventsCall { get; private set; }
    public int StatusCallCount { get; private set; }

    public Task<AppleBridgeResult<AppleBridgeStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        StatusCallCount++;
        return Task.FromResult(StatusResult);
    }

    public Task<AppleBridgeResult<AppleReminder>> CreateReminderAsync(
        string title, string? notes = null, DateTimeOffset? dueAt = null, int? priority = null,
        CancellationToken cancellationToken = default)
    {
        CreateCall = (title, notes, dueAt, priority);
        return Task.FromResult(CreateResult);
    }

    public Task<AppleBridgeResult<IReadOnlyList<AppleReminder>>> ListRemindersAsync(
        IReadOnlyList<string>? lists = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        ListCall = (lists, limit);
        return Task.FromResult(ListResult);
    }

    public Task<AppleBridgeResult<AppleReminderCompletion>> CompleteReminderAsync(
        string reminderId, CancellationToken cancellationToken = default)
    {
        CompleteCall = reminderId;
        return Task.FromResult(CompleteResult);
    }

    public Task<AppleBridgeResult<AppleCalendarEvent>> CreateCalendarEventAsync(
        string title, DateTimeOffset startAt, DateTimeOffset endAt,
        string? notes = null, string? timeZone = null, CancellationToken cancellationToken = default)
    {
        CreateEventCall = (title, startAt, endAt, notes, timeZone);
        return Task.FromResult(CreateEventResult);
    }

    public Task<AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>> ListCalendarEventsAsync(
        IReadOnlyList<string>? calendars = null, int? days = null, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ListEventsCall = (calendars, days, limit);
        return Task.FromResult(ListEventsResult);
    }
}
