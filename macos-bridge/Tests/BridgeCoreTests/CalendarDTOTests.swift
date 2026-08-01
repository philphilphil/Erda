import Foundation
import Testing

@testable import BridgeCore

/// Everything `POST /v1/calendar-events` refuses, and why. Every case here fails at **decode**,
/// before anything reaches EventKit — which is what makes it a 400 with no side effect rather than
/// a save that half happened.
@Suite("Calendar event request validation")
struct CalendarEventRequestTests {
    private func decode(_ body: String) throws -> CreateCalendarEventRequest {
        try StrictJSON.decode(CreateCalendarEventRequest.self, from: json(body))
    }

    private let minimal = #"""
    {"title":"Dentist","startAt":"2026-08-03T09:00:00+02:00","endAt":"2026-08-03T10:00:00+02:00"}
    """#

    @Test("a well-formed request decodes with its instants intact")
    func decodesMinimal() throws {
        let request = try decode(minimal)
        #expect(request.title == "Dentist")
        #expect(request.notes == nil)
        #expect(request.timeZone == nil)
        // +02:00, so 09:00 local is 07:00Z.
        #expect(request.startAt == Date(timeIntervalSince1970: 1_785_740_400))
        #expect(request.endAt.timeIntervalSince(request.startAt) == 3600)
    }

    @Test("every optional the request can express round-trips")
    func decodesFull() throws {
        let request = try decode(#"""
        {"title":"  Dentist  ","notes":"bring the referral","startAt":"2026-08-03T09:00:00+02:00","endAt":"2026-08-03T10:00:00+02:00","timeZone":"Europe/Berlin"}
        """#)
        // The title is trimmed; the notes are not, because a note's leading whitespace is the
        // user's own formatting.
        #expect(request.title == "Dentist")
        #expect(request.notes == "bring the referral")
        #expect(request.timeZone?.identifier == "Europe/Berlin")
    }

    // MARK: - The interval

    /// `end` strictly after `start`. A zero-length event is not something anyone means to create,
    /// and EventKit renders one as a point with no extent.
    @Test("an end at or before the start is refused", arguments: [
        // Inverted.
        (#"{"title":"x","startAt":"2026-08-03T10:00:00Z","endAt":"2026-08-03T09:00:00Z"}"#),
        // Equal — "strictly after" is the rule, not "not before".
        (#"{"title":"x","startAt":"2026-08-03T10:00:00Z","endAt":"2026-08-03T10:00:00Z"}"#),
    ])
    func refusesInvertedInterval(body: String) {
        #expect(throws: ApiError.invalidRequest) { try decode(body) }
    }

    @Test("an event exactly at the duration cap is accepted")
    func acceptsMaximumDuration() throws {
        let request = try decode(#"""
        {"title":"Sabbatical","startAt":"2026-08-03T00:00:00Z","endAt":"2026-08-10T00:00:00Z"}
        """#)
        #expect(request.endAt.timeIntervalSince(request.startAt) == Limits.eventMaxDuration)
    }

    /// One second past the cap. The bridge creates appointments; a month-long block is far more
    /// likely to be a model that got a date wrong than something anyone meant.
    @Test("an event one second longer than the cap is refused")
    func refusesOverlongEvent() {
        #expect(throws: ApiError.invalidRequest) {
            try decode(#"""
            {"title":"x","startAt":"2026-08-03T00:00:00Z","endAt":"2026-08-10T00:00:01Z"}
            """#)
        }
        #expect(throws: ApiError.invalidRequest) {
            try decode(#"""
            {"title":"x","startAt":"2026-08-03T00:00:00Z","endAt":"2026-11-03T00:00:00Z"}
            """#)
        }
    }

    // MARK: - Timestamps

    /// The same rule `dueAt` has, for the same reason: a naive timestamp would be read in whatever
    /// zone the Mac happens to be in, silently moving an appointment by hours.
    @Test("a timestamp without an explicit offset is refused", arguments: [
        #"{"title":"x","startAt":"2026-08-03T09:00:00","endAt":"2026-08-03T10:00:00Z"}"#,
        #"{"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00"}"#,
        #"{"title":"x","startAt":"2026-08-03","endAt":"2026-08-04"}"#,
        #"{"title":"x","startAt":"not a date","endAt":"2026-08-03T10:00:00Z"}"#,
    ])
    func refusesNaiveTimestamps(body: String) {
        #expect(throws: ApiError.invalidRequest) { try decode(body) }
    }

    @Test("both offset spellings are accepted, and mean the same instant", arguments: [
        ("+02:00", "2026-08-03T09:00:00+02:00"),
        ("+0200", "2026-08-03T09:00:00+0200"),
        ("Z", "2026-08-03T07:00:00Z"),
        ("fractional", "2026-08-03T07:00:00.000Z"),
    ])
    func acceptsOffsetSpellings(label: String, start: String) throws {
        let request = try decode(#"""
        {"title":"x","startAt":"\#(start)","endAt":"2026-08-03T09:00:00Z"}
        """#)
        #expect(request.startAt == Date(timeIntervalSince1970: 1_785_740_400), "\(label)")
    }

    // MARK: - Time zone

    /// Canonical IANA identifiers only.
    @Test("a canonical IANA identifier is accepted", arguments: [
        "Europe/Berlin", "America/New_York", "Australia/Sydney", "Asia/Tokyo", "GMT",
    ])
    func acceptsIanaZones(identifier: String) throws {
        let request = try decode(#"""
        {"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","timeZone":"\#(identifier)"}
        """#)
        #expect(request.timeZone?.identifier == identifier)
    }

    /// `UTC` is an alias Foundation resolves to `GMT`. It is accepted — a model will send it —
    /// and stored under the canonical spelling, so what goes back on the wire is `GMT`.
    @Test("an alias is canonicalised rather than refused")
    func canonicalisesAliases() throws {
        let request = try decode(#"""
        {"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","timeZone":"UTC"}
        """#)
        #expect(request.timeZone?.identifier == "GMT")
        #expect(request.timeZone == TimeZone(secondsFromGMT: 0))
    }

    @Test("anything that is not a canonical IANA identifier is refused", arguments: [
        "CEST",             // an abbreviation, ambiguous half the year
        "GMT+2",            // an offset dressed as a zone — parses, but is not a zone
        "PST",              // parses too, and is exactly as ambiguous as CEST
        "Europe/Nowhere",
        "europe/berlin",    // identifiers are case-sensitive; a fold would be a guess
        "",
        "Europe/Berlin ",
        "+02:00",
    ])
    func refusesNonIanaZones(identifier: String) {
        #expect(throws: ApiError.invalidRequest) {
            try decode(#"""
            {"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","timeZone":"\#(identifier)"}
            """#)
        }
    }

    /// An absent zone is not a floating event: the handler resolves it, so the event always
    /// carries one.
    @Test("an absent time zone falls back to the one the caller supplies")
    func fallsBackToDefaultZone() throws {
        let command = try decode(minimal).command(
            defaultTimeZone: try #require(TimeZone(identifier: "Europe/Berlin"))
        )
        #expect(command.timeZone.identifier == "Europe/Berlin")

        let explicit = try decode(#"""
        {"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","timeZone":"Asia/Tokyo"}
        """#).command(defaultTimeZone: try #require(TimeZone(identifier: "Europe/Berlin")))
        #expect(explicit.timeZone.identifier == "Asia/Tokyo")
    }

    // MARK: - Fields

    @Test("title and notes obey the same caps as a reminder's")
    func capsTitleAndNotes() throws {
        let atCap = String(repeating: "a", count: Limits.titleMaxLength)
        #expect(try decode(#"""
        {"title":"\#(atCap)","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z"}
        """#).title.count == Limits.titleMaxLength)

        for body in [
            #"{"title":"\#(atCap)a","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z"}"#,
            #"{"title":"   ","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z"}"#,
            #"{"title":"x","notes":"\#(String(repeating: "n", count: Limits.notesMaxLength + 1))","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z"}"#,
        ] {
            #expect(throws: ApiError.invalidRequest) { try decode(body) }
        }
    }

    /// Unknown keys are rejected, the same posture the reminder DTO takes. That is what makes an
    /// unsupported feature — a recurrence rule, an attendee list, an alarm — a loud 400 rather
    /// than a silently dropped field.
    @Test("an unknown key is refused, including one naming a deliberately unsupported feature", arguments: [
        #"{"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","titel":"typo"}"#,
        #"{"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","recurrence":"FREQ=DAILY"}"#,
        #"{"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","attendees":[]}"#,
        #"{"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","alarms":[]}"#,
        #"{"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","allDay":true}"#,
        #"{"title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z","location":"here"}"#,
    ])
    func refusesUnknownKeys(body: String) {
        #expect(throws: ApiError.invalidRequest) { try decode(body) }
    }

    /// Note which field is **not** in here: there is no "missing calendar" case any more, because
    /// naming no calendar is now the only correct way to ask for an event.
    @Test("a missing required field is refused", arguments: [
        #"{"startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z"}"#,
        #"{"title":"x","endAt":"2026-08-03T10:00:00Z"}"#,
        #"{"title":"x","startAt":"2026-08-03T09:00:00Z"}"#,
        #"{}"#,
        #"not json"#,
    ])
    func refusesMissingFields(body: String) {
        #expect(throws: ApiError.invalidRequest) { try decode(body) }
    }

    /// The command carries no id and no calendar, and there is no route that would take either.
    /// This is the assertion that fails if somebody later adds a `BridgeID` "for symmetry" with the
    /// reminder path, or puts a calendar back so a caller can steer the write.
    @Test("the command has no event id and no calendar, because no caller may supply one")
    func commandHasNoIdAndNoCalendar() throws {
        let command = try decode(minimal).command(defaultTimeZone: try #require(TimeZone(identifier: "GMT")))
        let labels = Mirror(reflecting: command).children.compactMap(\.label)
        #expect(labels == ["title", "notes", "startAt", "endAt", "timeZone"])
    }

    /// The wire-format half of the same rule. `calendar` used to be a required key, so a client
    /// built against the old shape will keep sending it — and it has to be **told**, not quietly
    /// obeyed and not quietly ignored. Strict decoding already does this; the test is here so it
    /// cannot be relaxed by accident.
    @Test("a request that still names a calendar is refused, not silently ignored", arguments: [
        #"{"calendar":"Privat","title":"x","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z"}"#,
        // Including one naming a calendar that would be perfectly valid — it is the *key* that is
        // gone, not the value that is wrong.
        #"{"title":"x","calendar":"Arbeit","startAt":"2026-08-03T09:00:00Z","endAt":"2026-08-03T10:00:00Z"}"#,
    ])
    func refusesACalendarKey(body: String) {
        #expect(throws: ApiError.invalidRequest) { try decode(body) }
    }
}

@Suite("Calendar event query validation")
struct CalendarEventQueryTests {
    @Test("the defaults are the documented ones")
    func defaults() throws {
        let query = try ListCalendarEventsQuery()
        #expect(query.calendars.isEmpty)
        #expect(query.days == Limits.eventWindowDefaultDays)
        #expect(query.limit == Limits.eventLimitDefault)
        #expect(query.days == 7)
        #expect(query.limit == 50)
    }

    @Test("the window is measured from the instant it is given, not from a stored one")
    func windowIsRelative() throws {
        let now = Date(timeIntervalSince1970: 1_780_000_000)
        let window = try ListCalendarEventsQuery(days: 3).window(from: now)
        #expect(window.start == now)
        #expect(window.end == now.addingTimeInterval(3 * 24 * 60 * 60))
    }

    @Test("the window and the limit are both capped and both have a floor", arguments: [
        (0, Limits.eventLimitDefault),
        (Limits.eventWindowMaxDays + 1, Limits.eventLimitDefault),
        (-1, Limits.eventLimitDefault),
        (Limits.eventWindowDefaultDays, 0),
        (Limits.eventWindowDefaultDays, Limits.eventLimitMax + 1),
        (Limits.eventWindowDefaultDays, -1),
    ])
    func refusesOutOfRange(days: Int, limit: Int) {
        #expect(throws: ApiError.invalidRequest) {
            try ListCalendarEventsQuery(days: days, limit: limit)
        }
    }

    @Test("the extremes of both ranges are accepted")
    func acceptsBoundaries() throws {
        #expect(try ListCalendarEventsQuery(days: Limits.eventWindowMinDays).days == 1)
        #expect(try ListCalendarEventsQuery(days: Limits.eventWindowMaxDays).days == 31)
        #expect(try ListCalendarEventsQuery(limit: 1).limit == 1)
        #expect(try ListCalendarEventsQuery(limit: Limits.eventLimitMax).limit == 200)
    }
}
