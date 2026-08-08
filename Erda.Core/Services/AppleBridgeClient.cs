using System.Net.Http.Headers;
using System.Text.Json;
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services;

/// <summary>One Apple Reminders task as reported by the bridge. Mirrors the wire shape of the
/// Swift <c>ReminderSnapshot</c> (macos-bridge/Sources/BridgeCore/Model/ReminderDTOs.swift):
/// <c>id</c> is a bridge-issued id (<c>rem_&lt;uuid&gt;</c>), never an EventKit identifier, and
/// <c>list</c> is the list's name as it reads in Reminders.app.</summary>
public sealed record AppleReminder(
    string Id,
    string List,
    string Title,
    string? Notes,
    DateTimeOffset? DueAt,
    int Priority,
    bool IsCompleted,
    DateTimeOffset? CompletedAt);

/// <summary>The result of completing a reminder. <see cref="AlreadyCompleted"/> is true when the
/// reminder was already done — the bridge treats that as a success no-op, not an error.</summary>
public sealed record AppleReminderCompletion(string Id, bool AlreadyCompleted);

/// <summary>One Apple Calendar event as reported by the bridge. Mirrors the wire shape of the Swift
/// <c>CalendarEventSnapshot</c> (macos-bridge/Sources/BridgeCore/Model/CalendarDTOs.swift):
/// <c>Calendar</c> is the calendar's name as it reads in Calendar.app, and there is deliberately
/// <b>no id</b> — the bridge has no route that takes one, since events cannot be edited or deleted
/// through it. <see cref="TimeZone"/> is the event's own IANA zone and is null for a floating
/// event.
/// <para>
/// <see cref="StartDay"/>/<see cref="EndDay"/> are <c>yyyy-MM-dd</c> calendar dates and are sent
/// <b>only</b> for an all-day event, whose instants cannot express the day on their own: EventKit
/// anchors an all-day event to whatever zone the Mac is in and leaves its zone null, so
/// <c>2026-08-10T22:00:00Z</c> is a birthday Calendar.app draws on the 11th. Only the Mac knows the
/// anchoring zone, so only the Mac can state the day — deriving one here reports every birthday a
/// day early. <see cref="EndDay"/> is the <i>inclusive</i> last day. Both are null for a timed
/// event, and both are null when talking to a bridge older than this field pair.
/// </para></summary>
public sealed record AppleCalendarEvent(
    string Calendar,
    string Title,
    string? Notes,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    bool IsAllDay,
    string? TimeZone,
    string? StartDay = null,
    string? EndDay = null);

/// <summary>The one calendar the bridge writes events to, picked by Phil in the ErdaBridge app on
/// the Mac and settable nowhere else. <see cref="State"/> is <c>ok</c>, <c>not_configured</c> (he has
/// never chosen one) or <c>unresolvable</c> (the one he chose is no longer on the Mac); the last two
/// both make <c>create_calendar_event</c> fail with <c>calendar_not_configured</c>.
/// <see cref="Name"/> is null only when nothing was ever chosen.</summary>
public sealed record AppleWriteCalendar(string State, string? Name);

/// <summary>The bridge's <c>GET /v1/status</c> response. Reminders and Calendar are reported
/// separately because macOS authorizes them separately — one can be usable while the other is not,
/// so a single verdict would have to lie about one of them. <see cref="Lists"/> are the names a
/// caller may address; <see cref="Calendars"/> are the names a <i>listing</i> may filter by, which
/// is not the same as where events go — that is <see cref="WriteCalendar"/>, and it is chosen on the
/// Mac.</summary>
public sealed record AppleBridgeStatus(
    string Availability,
    IReadOnlyList<string> Lists,
    string CalendarAvailability,
    IReadOnlyList<string> Calendars,
    AppleWriteCalendar? WriteCalendar = null);

/// <summary>
/// The outcome of one <see cref="IAppleBridgeClient"/> call. Never an exception — like
/// <see cref="Erda.Core.WhatsApp.IWhatsAppSender"/>, a failure (bad config, bridge error, or the Mac
/// being asleep/off the LAN) comes back as <see cref="Success"/> = false with a readable
/// <see cref="Error"/>, so a tool can relay it to Phil instead of the turn blowing up.
/// </summary>
public sealed class AppleBridgeResult<T>
{
    public bool Success { get; }
    public T? Value { get; }
    public string? Error { get; }

    private AppleBridgeResult(bool success, T? value, string? error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    public static AppleBridgeResult<T> Ok(T value) => new(true, value, null);
    public static AppleBridgeResult<T> Fail(string error) => new(false, default, error);
}

/// <summary>Client for the macOS ErdaBridge HTTP API: create/list/complete Apple Reminders, plus
/// create/list Apple Calendar events. Reminder lists are addressed by their real name and the bridge
/// reaches all of them; calendars are <i>readable</i> the same way, but events are always created in
/// the single calendar Phil pinned in the ErdaBridge app — no request names one. See
/// <see cref="AppleBridgeOptions"/> for configuration and macos-bridge/README.md for why (including
/// why calendar access is full read, not write-only).</summary>
public interface IAppleBridgeClient
{
    /// <summary>Checks whether the bridge can currently serve requests — Reminders and Calendar
    /// access are reported separately, since macOS grants them separately — and which list and
    /// calendar names exist on the Mac.</summary>
    Task<AppleBridgeResult<AppleBridgeStatus>> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a reminder in the named list. <paramref name="list"/> is the list's name as it
    /// reads in Reminders.app — there is no default list, and a name that matches no list (or is
    /// ambiguous across two accounts) fails rather than landing somewhere plausible.</summary>
    Task<AppleBridgeResult<AppleReminder>> CreateReminderAsync(
        string list,
        string title,
        string? notes = null,
        DateTimeOffset? dueAt = null,
        int? priority = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists incomplete reminders. Omitting <paramref name="lists"/> lists every reminder
    /// list on the Mac.</summary>
    Task<AppleBridgeResult<IReadOnlyList<AppleReminder>>> ListRemindersAsync(
        IReadOnlyList<string>? lists = null,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a reminder complete by its bridge-issued id. Completing an already-completed
    /// reminder succeeds as a no-op (<see cref="AppleReminderCompletion.AlreadyCompleted"/> = true).</summary>
    Task<AppleBridgeResult<AppleReminderCompletion>> CompleteReminderAsync(
        string reminderId, CancellationToken cancellationToken = default);

    /// <summary>Creates an event in the <b>one</b> calendar Phil pinned in the ErdaBridge app on the
    /// Mac. There is deliberately no calendar parameter: the wire format carries none, the choice
    /// lives on the Mac, and no request can change or override it. If he has pinned none — or the
    /// one he pinned is gone — this fails with <c>calendar_not_configured</c> rather than landing
    /// somewhere plausible. The returned <see cref="AppleCalendarEvent.Calendar"/> reports which
    /// calendar it went into.
    /// <para>
    /// <paramref name="startAt"/>/<paramref name="endAt"/> must carry a real offset (the bridge
    /// refuses a naive timestamp), <paramref name="endAt"/> must be after <paramref name="startAt"/>,
    /// and the event may not exceed seven days. <paramref name="timeZone"/> is an optional IANA
    /// identifier (e.g. <c>Europe/Berlin</c>) deciding which wall-clock time the event displays at.
    /// </para></summary>
    Task<AppleBridgeResult<AppleCalendarEvent>> CreateCalendarEventAsync(
        string title,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        string? notes = null,
        string? timeZone = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists upcoming events, starting now. Reads are <i>not</i> pinned the way writes are:
    /// omitting <paramref name="calendars"/> spans every calendar on the Mac, and naming one narrows
    /// to it. <paramref name="days"/> is the window length (the bridge's default applies if omitted;
    /// it caps both the window and the count).</summary>
    Task<AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>> ListCalendarEventsAsync(
        IReadOnlyList<string>? calendars = null,
        int? days = null,
        int? limit = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP implementation of <see cref="IAppleBridgeClient"/>. Sends <c>Authorization: Bearer
/// &lt;ApiKey&gt;</c> on every request (including status) and a fresh <c>Idempotency-Key</c> per
/// mutating call (create/complete), per the bridge's design. Never throws: a bad response, an
/// unreachable bridge (the Mac asleep or off the LAN), or a malformed body all come back as a failed
/// <see cref="AppleBridgeResult{T}"/> with a message safe to relay to Phil — following the same
/// never-throws idiom as <see cref="Erda.Core.WhatsApp.WhatsAppSender"/>.
/// </summary>
public sealed class AppleBridgeClient(
    HttpClient http,
    IOptions<AppleBridgeOptions> options,
    ILogger<AppleBridgeClient> logger) : IAppleBridgeClient
{
    private const string TransportFailureMessage =
        "Couldn't reach the ErdaBridge app on the Mac — it may be asleep, off the LAN, or not running.";

    // System.Text.Json's "Web" defaults (camelCase property names) match the bridge's wire format
    // (list, title, notes, dueAt, priority, isCompleted, completedAt, ...) without per-property
    // [JsonPropertyName] attributes — see ScryfallClient for the same convention.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Every method here must `await` inside the `using`. Returning the Task instead disposes the
    // request — and with it the JsonContent body — before HttpClient has written it to the socket,
    // which surfaces as an ObjectDisposedException wrapped in HttpRequestException, i.e. as a
    // transport failure that looks exactly like "the Mac is asleep".
    public async Task<AppleBridgeResult<AppleBridgeStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBuildUrl("/v1/status", out var url, out var configError))
            return AppleBridgeResult<AppleBridgeStatus>.Fail(configError!);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);
        return await SendAsync<AppleBridgeStatus>(request, "check status", cancellationToken);
    }

    public async Task<AppleBridgeResult<AppleReminder>> CreateReminderAsync(
        string list,
        string title,
        string? notes = null,
        DateTimeOffset? dueAt = null,
        int? priority = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildUrl("/v1/reminders", out var url, out var configError))
            return AppleBridgeResult<AppleReminder>.Fail(configError!);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { list, title, notes, dueAt, priority }, options: Json),
        };
        ApplyAuth(request);
        ApplyIdempotencyKey(request);
        return await SendAsync<AppleReminder>(request, "create reminder", cancellationToken);
    }

    public async Task<AppleBridgeResult<IReadOnlyList<AppleReminder>>> ListRemindersAsync(
        IReadOnlyList<string>? lists = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildUrl("/v1/reminders", out var baseUrl, out var configError))
            return AppleBridgeResult<IReadOnlyList<AppleReminder>>.Fail(configError!);

        // GET /v1/reminders takes a repeated ?list=x&list=y query parameter (matching
        // ListRemindersQuery.lists; omitted means every reminder list on the Mac) plus ?limit=n.
        // EscapeDataString, not a raw name: list names hold spaces and non-ASCII, and the bridge
        // percent-decodes this value (a space must arrive as %20, never as +).
        var query = new List<string>();
        foreach (var list in lists ?? [])
            if (!string.IsNullOrWhiteSpace(list))
                query.Add($"list={Uri.EscapeDataString(list.Trim())}");
        if (limit is > 0)
            query.Add($"limit={limit.Value}");

        var url = query.Count == 0 ? baseUrl : $"{baseUrl}?{string.Join("&", query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);

        // The response is a wrapper object, {"items":[...]}, not a bare array — so the shape can gain
        // a field later without breaking this client.
        var result = await SendAsync<ReminderListResponse>(request, "list reminders", cancellationToken);
        return result.Success
            ? AppleBridgeResult<IReadOnlyList<AppleReminder>>.Ok(result.Value!.Items)
            : AppleBridgeResult<IReadOnlyList<AppleReminder>>.Fail(result.Error!);
    }

    public async Task<AppleBridgeResult<AppleReminderCompletion>> CompleteReminderAsync(
        string reminderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reminderId))
            return AppleBridgeResult<AppleReminderCompletion>.Fail("No reminder id was given.");

        if (!TryBuildUrl($"/v1/reminders/{Uri.EscapeDataString(reminderId.Trim())}/complete", out var url, out var configError))
            return AppleBridgeResult<AppleReminderCompletion>.Fail(configError!);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyAuth(request);
        ApplyIdempotencyKey(request);
        return await SendAsync<AppleReminderCompletion>(request, "complete reminder", cancellationToken);
    }

    public async Task<AppleBridgeResult<AppleCalendarEvent>> CreateCalendarEventAsync(
        string title,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        string? notes = null,
        string? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildUrl("/v1/calendar-events", out var url, out var configError))
            return AppleBridgeResult<AppleCalendarEvent>.Fail(configError!);

        // No `calendar` field, and adding one back would not be a no-op: the bridge decodes this
        // body strictly, so an unknown key is a 400. The write target lives on the Mac.
        //
        // Note the `await` inside the `using` — see the comment on GetStatusAsync. A DateTimeOffset
        // serializes with its offset intact ("+02:00"), which is exactly what the bridge requires:
        // it refuses a timestamp with no offset outright.
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(
                new { title, notes, startAt, endAt, timeZone }, options: Json),
        };
        ApplyAuth(request);
        ApplyIdempotencyKey(request);
        return await SendAsync<AppleCalendarEvent>(request, "create calendar event", cancellationToken);
    }

    public async Task<AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>> ListCalendarEventsAsync(
        IReadOnlyList<string>? calendars = null,
        int? days = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildUrl("/v1/calendar-events", out var baseUrl, out var configError))
            return AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Fail(configError!);

        // GET /v1/calendar-events takes a repeated ?calendar=x&calendar=y (omitted means every
        // calendar) plus ?days=n and ?limit=n — reads span every calendar, unlike creates, which go
        // to the one pinned on the Mac. EscapeDataString for the same reason as the reminder route:
        // calendar names hold spaces and non-ASCII, and a space must arrive as %20, never +.
        var query = new List<string>();
        foreach (var calendar in calendars ?? [])
            if (!string.IsNullOrWhiteSpace(calendar))
                query.Add($"calendar={Uri.EscapeDataString(calendar.Trim())}");
        if (days is > 0)
            query.Add($"days={days.Value}");
        if (limit is > 0)
            query.Add($"limit={limit.Value}");

        var url = query.Count == 0 ? baseUrl : $"{baseUrl}?{string.Join("&", query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);

        // Wrapper object, {"items":[...]}, not a bare array — same convention as the reminder list.
        var result = await SendAsync<CalendarEventListResponse>(request, "list calendar events", cancellationToken);
        return result.Success
            ? AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok(result.Value!.Items)
            : AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Fail(result.Error!);
    }

    private bool TryBuildUrl(string path, out string url, out string? error)
    {
        var baseUrl = options.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogWarning("Apple bridge base URL is not configured; cannot call {Path}.", path);
            url = "";
            error = "The Apple Reminders bridge is not configured (AppleBridge__BaseUrl is unset).";
            return false;
        }

        url = $"{baseUrl.TrimEnd('/')}{path}";
        error = null;
        return true;
    }

    private void ApplyAuth(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);

    private static void ApplyIdempotencyKey(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());

    private async Task<AppleBridgeResult<T>> SendAsync<T>(HttpRequestMessage request, string action, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
                if (value is null)
                {
                    logger.LogWarning("Apple bridge returned an empty/unparseable body for {Action}.", action);
                    return AppleBridgeResult<T>.Fail("The bridge returned an unexpected empty response.");
                }
                return AppleBridgeResult<T>.Ok(value);
            }

            var errorBody = await TryReadErrorAsync(response, cancellationToken);
            logger.LogWarning(
                "Apple bridge returned {Status} ({Code}) for {Action}.",
                (int)response.StatusCode, errorBody?.Error ?? "(unparseable)", action);
            return AppleBridgeResult<T>.Fail(MapError(errorBody?.Error));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reach the Apple Reminders bridge at {Url} for {Action}.", request.RequestUri, action);
            return AppleBridgeResult<T>.Fail(TransportFailureMessage);
        }
    }

    private async Task<ApiErrorResponse?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Json, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the bridge's closed error-code set (macos-bridge/Sources/BridgeCore/Model/ApiError.swift)
    /// to a short message safe to relay to Phil. Categories that call for different fixes must read
    /// differently: <c>no_such_list</c>/<c>list_read_only</c> mean the list Erda named is wrong (retry
    /// with a different name), <c>reminders_unavailable</c> points at macOS Reminders permission, and
    /// on the calendar side <c>no_such_calendar</c> (check the name in a listing filter),
    /// <c>ambiguous_calendar</c> (two calendars wear that name — rename one), <c>calendar_read_only</c>
    /// (the pinned calendar is subscribed/holiday), <c>calendar_unavailable</c> (macOS
    /// <i>Calendars</i> permission, a different System Settings row from Reminders) and
    /// <c>calendar_not_configured</c> (no write calendar is pinned in the ErdaBridge app, or the
    /// pinned one is gone) are five separate fixes and never share wording. The last two are both
    /// 503s and are the pair most easily confused: one is a permission on the Mac, one is a choice
    /// in the app, and neither is "the Mac is unreachable" — that is
    /// <see cref="TransportFailureMessage"/>, which a caught network exception produces without ever
    /// reaching this method.
    /// </summary>
    private static string MapError(string? code) => code switch
    {
        "invalid_request" => "The bridge rejected the request as malformed — this looks like an Erda bug.",
        "unauthorized" => "The bridge rejected the API key — check AppleBridge__ApiKey matches the token shown in ErdaBridge's setup UI on the Mac.",
        "not_found" => "No reminder with that id was found on the Mac (it may have been completed, deleted, or moved to a list that no longer exists).",
        "method_not_allowed" => "The bridge rejected the request (unsupported method) — this looks like an Erda bug.",
        "unsupported_media_type" => "The bridge rejected the request (unsupported content type) — this looks like an Erda bug.",
        "unsupported_http_version" => "The bridge rejected the request (unsupported HTTP version) — this looks like an Erda bug.",
        "payload_too_large" => "The reminder text is too long for the bridge to accept.",
        "rate_limited" => "The bridge is rate-limiting requests right now — try again in a moment.",
        "idempotency_key_reuse" => "The bridge saw a conflicting duplicate request — try again.",
        "request_in_progress" => "That request is already being processed on the Mac — try again shortly.",
        "no_such_list" => "There's no Reminders list with that name on the Mac — check the exact name in Reminders.app (or list reminders to see the names). If two accounts both have a list with that name, rename one: the bridge won't guess between them.",
        "list_read_only" => "That Reminders list is read-only, so nothing can be added to it — pick a different list.",
        "reminders_unavailable" => "The Mac has revoked (or never granted) Reminders access to ErdaBridge — check Reminders permission in System Settings on the Mac.",
        "no_such_calendar" => "There's no calendar with that name on the Mac — check the exact name in Calendar.app.",
        "ambiguous_calendar" => "Two calendars on the Mac have that exact name (e.g. one in iCloud and one local), so the bridge won't guess between them — rename one in Calendar.app, or name the other calendar instead.",
        "calendar_read_only" => "The calendar ErdaBridge is set to write to is read-only (a subscribed or holiday calendar), so nothing can be added to it — choose a different one in the ErdaBridge app on the Mac.",
        "calendar_unavailable" => "The Mac has revoked (or never granted) Calendar access to ErdaBridge — check Calendars permission in System Settings on the Mac. (That's a different setting from Reminders.)",
        "calendar_not_configured" => "No calendar is set up for writing on the Mac — open the ErdaBridge app there and choose which calendar events should go into. (The Mac is reachable and Calendar access is fine; it just hasn't been told where to write. Reading the calendar still works.)",
        "internal" => "The bridge hit an internal error — check its logs on the Mac.",
        _ => "The Apple bridge returned an unexpected error.",
    };

    /// <summary>The bridge's error envelope: <c>{"error":"&lt;snake_code&gt;","requestId":"…"}</c> — no
    /// message field by design (see ApiError.swift), so detail here is limited to the code.</summary>
    private sealed record ApiErrorResponse(string Error, string RequestId);

    /// <summary>The wrapper body of <c>GET /v1/reminders</c>: <c>{"items":[...]}</c>.</summary>
    private sealed record ReminderListResponse(IReadOnlyList<AppleReminder> Items);

    /// <summary>The wrapper body of <c>GET /v1/calendar-events</c>: <c>{"items":[...]}</c>.</summary>
    private sealed record CalendarEventListResponse(IReadOnlyList<AppleCalendarEvent> Items);
}
