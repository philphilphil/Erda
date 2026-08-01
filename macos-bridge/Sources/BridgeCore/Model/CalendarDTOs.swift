import Foundation

// MARK: - Wire request

/// The body of `POST /v1/calendar-events`, decoded strictly: unknown keys are rejected, every
/// field is capped, and both timestamps must carry an explicit UTC offset.
///
/// There is deliberately no `allDay` flag, no recurrence, no attendees, no alarms and no
/// location. The bridge creates one timed appointment in one calendar; anything richer than that
/// is a job for Calendar.app, not for a LAN API with a bearer token on it.
public struct CreateCalendarEventRequest: Sendable, Equatable, Decodable {
    /// The calendar's name as it reads in Calendar.app. Required: there is no default calendar,
    /// and a name that matches nothing fails rather than landing somewhere plausible.
    public let calendar: CalendarName
    /// Trimmed, 1…512 — the same cap a reminder title gets.
    public let title: String
    /// ≤ 4096, kept verbatim (not trimmed).
    public let notes: String?
    /// Both instants are offset-bearing, so the appointment is unambiguous before it reaches
    /// EventKit. `endAt` must be strictly after `startAt` and at most `Limits.eventMaxDuration`
    /// later.
    public let startAt: Date
    public let endAt: Date
    /// The IANA zone the event is *expressed in* once it reaches Calendar.app. Optional: the
    /// instants above already pin the appointment, so this only decides which wall-clock time the
    /// user sees. Absent means the bridge's own zone.
    public let timeZone: TimeZone?

    static let allowedKeys: Set<String> = ["calendar", "title", "notes", "startAt", "endAt", "timeZone"]

    public init(
        calendar: CalendarName,
        title: String,
        notes: String? = nil,
        startAt: Date,
        endAt: Date,
        timeZone: TimeZone? = nil
    ) {
        self.calendar = calendar
        self.title = title
        self.notes = notes
        self.startAt = startAt
        self.endAt = endAt
        self.timeZone = timeZone
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: AnyCodingKey.self)
        try StrictDecoding.rejectUnknownKeys(in: container, allowed: Self.allowedKeys)

        self.calendar = try container.decode(CalendarName.self, forKey: "calendar")
        self.title = try Validate.title(container.decode(String.self, forKey: "title"))
        self.notes = try container.decodeIfPresent(String.self, forKey: "notes").map(Validate.notes)
        self.startAt = try container.decode(Date.self, forKey: "startAt")
        self.endAt = try container.decode(Date.self, forKey: "endAt")
        // Decoded as a string and validated here rather than through `TimeZone`'s own `Codable`
        // conformance, whose wire shape is Foundation's business and not a contract this API
        // wants to inherit.
        self.timeZone = try container.decodeIfPresent(String.self, forKey: "timeZone")
            .map(Validate.timeZone)

        // Cross-field, and therefore here rather than in a per-field validator: a decode failure
        // is what turns it into a 400 before anything reaches EventKit.
        try Validate.eventInterval(start: startAt, end: endAt)
    }

    /// Pairs the validated request with the zone to fall back on when the caller named none.
    public func command(defaultTimeZone: TimeZone) -> CreateCalendarEventCommand {
        CreateCalendarEventCommand(
            calendar: calendar,
            title: title,
            notes: notes,
            startAt: startAt,
            endAt: endAt,
            timeZone: timeZone ?? defaultTimeZone
        )
    }
}

// MARK: - Service-facing commands (the `CalendarService` seam)

/// A validated create, with the zone already resolved.
///
/// Unlike `CreateReminderCommand` this carries **no `BridgeID`**: there is no route that takes an
/// event id — no complete, no edit, no delete — so an id would be a handle to nothing, and
/// minting one would imply an operation the bridge deliberately does not have.
public struct CreateCalendarEventCommand: Sendable, Equatable {
    public let calendar: CalendarName
    public let title: String
    public let notes: String?
    public let startAt: Date
    public let endAt: Date
    public let timeZone: TimeZone

    public init(
        calendar: CalendarName,
        title: String,
        notes: String?,
        startAt: Date,
        endAt: Date,
        timeZone: TimeZone
    ) {
        self.calendar = calendar
        self.title = title
        self.notes = notes
        self.startAt = startAt
        self.endAt = endAt
        self.timeZone = timeZone
    }
}

/// `GET /v1/calendar-events`. Built from query parameters, not a JSON body, so it validates in its
/// initialiser instead of going through `StrictJSON`.
///
/// The window always starts *now* — "upcoming" is the only question this route answers, and a
/// caller-supplied start would need a zone to be meaningful and would let the route be used to
/// trawl history. `days` narrows or widens it within `Limits.eventWindow…`.
public struct ListCalendarEventsQuery: Sendable, Equatable {
    /// Empty means every calendar on this Mac.
    public let calendars: [CalendarName]
    public let days: Int
    public let limit: Int

    public init(
        calendars: [CalendarName] = [],
        days: Int = Limits.eventWindowDefaultDays,
        limit: Int = Limits.eventLimitDefault
    ) throws {
        self.calendars = calendars
        self.days = try Validate.eventWindowDays(days)
        self.limit = try Validate.eventLimit(limit)
    }

    /// The window this query covers, measured from `now`.
    public func window(from now: Date) -> (start: Date, end: Date) {
        (now, now.addingTimeInterval(TimeInterval(days) * 24 * 60 * 60))
    }
}

// MARK: - Wire responses

/// One calendar event as the client sees it. Carries the calendar's name, never its
/// `calendarIdentifier`, and no event identifier at all — see `CreateCalendarEventCommand`.
public struct CalendarEventSnapshot: Sendable, Equatable, Codable {
    public let calendar: CalendarName
    public let title: String
    public let notes: String?
    public let startAt: Date
    public let endAt: Date
    /// True for an event that occupies whole days rather than a span of clock time. The bridge
    /// never *creates* one — a create always carries explicit times — but Calendar.app is full of
    /// them, and a caller told only "starts at 22:00Z" would report a birthday at the wrong time.
    public let isAllDay: Bool
    /// The event's own IANA zone, when it has one. `EKEvent.timeZone` is genuinely optional —
    /// a floating event has none — and inventing one here would be a claim the data does not make.
    public let timeZone: String?

    public init(
        calendar: CalendarName,
        title: String,
        notes: String? = nil,
        startAt: Date,
        endAt: Date,
        isAllDay: Bool = false,
        timeZone: String? = nil
    ) {
        self.calendar = calendar
        self.title = title
        self.notes = notes
        self.startAt = startAt
        self.endAt = endAt
        self.isAllDay = isAllDay
        self.timeZone = timeZone
    }
}

/// The body of `GET /v1/calendar-events`.
///
/// A wrapper object rather than a bare top-level array, for the same reason `ListRemindersResponse`
/// is one: an array can never gain a field later (a cursor, a truncation flag) without breaking
/// every client that already parses it. That is not hypothetical here — the list route shipped a
/// bare array once and no test could see it, which is what `WireFormatTests` exists for.
public struct ListCalendarEventsResponse: Sendable, Equatable, Codable {
    public let items: [CalendarEventSnapshot]

    public init(items: [CalendarEventSnapshot]) {
        self.items = items
    }
}
