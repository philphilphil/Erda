import Foundation

/// An in-memory `CalendarService` that reproduces the real one's *contract*: a name that matches
/// no calendar fails closed, a name matching two fails differently, a read-only calendar cannot
/// take an event, the window really does bound what `upcoming` returns, and availability is
/// switchable.
///
/// It lives in the library, not in `BridgeCoreTests`, for the same reason `FakeReminders` does: a
/// test target's types are not importable from another module, and the `BridgeHTTP` tests need
/// this one to drive the whole handler path without EventKit.
public actor FakeCalendar: CalendarService {
    /// The calendars this pretend Mac has. Nothing outside it resolves, and there is no default.
    private var knownCalendars: Set<CalendarName>
    /// Names that resolve to *two* calendars — a Mac with an iCloud "Privat" and a local one.
    /// Held as names rather than as duplicate entries because the fake has no account model; what
    /// it reproduces is the outcome, which is that the name cannot be resolved to one calendar.
    private var ambiguousCalendars: Set<CalendarName>
    /// Calendars that exist but cannot take a new event — subscribed and holiday calendars.
    private var readOnlyCalendars: Set<CalendarName>
    private var stored: [CalendarEventSnapshot] = []
    private var currentAvailability: CalendarAvailability
    /// When set, every call throws this instead of doing anything — for exercising error paths.
    private var forcedError: ApiError?
    private let clock: any BridgeClock

    public init(
        calendars: Set<CalendarName> = [],
        readOnly: Set<CalendarName> = [],
        ambiguous: Set<CalendarName> = [],
        availability: CalendarAvailability = .ok,
        seeded: [CalendarEventSnapshot] = [],
        clock: any BridgeClock = SystemClock()
    ) {
        self.knownCalendars = calendars
        self.readOnlyCalendars = readOnly
        self.ambiguousCalendars = ambiguous
        self.currentAvailability = availability
        self.stored = seeded
        self.clock = clock
    }

    // MARK: - Test controls

    public func setAvailability(_ availability: CalendarAvailability) {
        currentAvailability = availability
    }

    public func setForcedError(_ error: ApiError?) {
        forcedError = error
    }

    public func markReadOnly(_ calendar: CalendarName) {
        readOnlyCalendars.insert(calendar)
    }

    public func markAmbiguous(_ calendar: CalendarName) {
        ambiguousCalendars.insert(calendar)
    }

    public func seed(_ event: CalendarEventSnapshot) {
        stored.append(event)
    }

    public var all: [CalendarEventSnapshot] {
        stored.sorted { $0.startAt < $1.startAt }
    }

    // MARK: - CalendarService

    public func calendarAvailability() async -> CalendarAvailability {
        currentAvailability
    }

    public func availableCalendars() async -> [CalendarName] {
        currentAvailability == .ok ? knownCalendars.sorted() : []
    }

    public func upcoming(_ query: ListCalendarEventsQuery) async throws -> [CalendarEventSnapshot] {
        try preflight()
        // No name given means every calendar on this Mac.
        let requested = query.calendars.isEmpty ? knownCalendars : Set(query.calendars)
        for calendar in query.calendars {
            try check(calendar)
        }

        let window = query.window(from: clock.now)
        return stored
            .filter { requested.contains($0.calendar) }
            // Half-open, matching `predicateForEvents(withStart:end:calendars:)`'s own reading of
            // its bounds closely enough for the contract that matters: an event beyond the window
            // is not returned.
            .filter { $0.endAt > window.start && $0.startAt < window.end }
            .sorted { ($0.startAt, $0.title) < ($1.startAt, $1.title) }
            .prefix(query.limit)
            .map { $0 }
    }

    public func create(_ command: CreateCalendarEventCommand) async throws -> CalendarEventSnapshot {
        try preflight()
        try check(command.calendar)
        guard !readOnlyCalendars.contains(command.calendar) else { throw ApiError.calendarReadOnly }

        let snapshot = CalendarEventSnapshot(
            calendar: command.calendar,
            title: command.title,
            notes: command.notes,
            startAt: command.startAt,
            endAt: command.endAt,
            isAllDay: false,
            timeZone: command.timeZone.identifier
        )
        stored.append(snapshot)
        return snapshot
    }

    // MARK: - Internals

    private func preflight() throws {
        if let forcedError { throw forcedError }
        if let unavailable = currentAvailability.apiError { throw unavailable }
    }

    private func check(_ calendar: CalendarName) throws {
        // Ambiguity is checked first: a name wearing two calendars is also "known", and the
        // caller has to be told which of the two problems it has.
        guard !ambiguousCalendars.contains(calendar) else { throw ApiError.ambiguousCalendar }
        guard knownCalendars.contains(calendar) else { throw ApiError.noSuchCalendar }
    }
}
