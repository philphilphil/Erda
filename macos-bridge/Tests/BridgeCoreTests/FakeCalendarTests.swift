import Foundation
import Testing

@testable import BridgeCore

/// `FakeCalendar` is what the whole HTTP surface is exercised against, so its *contract* has to
/// match the real one — a fake that fails differently would make those tests worthless. These
/// pin the four behaviours that matter: fail closed on an unknown name, fail *differently* on an
/// ambiguous one, refuse a read-only calendar, and honour the window.
@Suite("FakeCalendar")
struct FakeCalendarTests {
    private let now = Date(timeIntervalSince1970: 1_780_000_000)

    private func fake(
        calendars: [String] = ["Privat", "Arbeit"],
        readOnly: [String] = [],
        ambiguous: [String] = []
    ) throws -> FakeCalendar {
        FakeCalendar(
            calendars: Set(try calendars.map { try calendarName($0) }),
            readOnly: Set(try readOnly.map { try calendarName($0) }),
            ambiguous: Set(try ambiguous.map { try calendarName($0) }),
            clock: ManualClock(now: now)
        )
    }

    private func command(
        _ calendar: String = "Privat",
        title: String = "Dentist",
        startingIn hours: Double = 24
    ) throws -> CreateCalendarEventCommand {
        let start = now.addingTimeInterval(hours * 3600)
        return CreateCalendarEventCommand(
            calendar: try calendarName(calendar),
            title: title,
            notes: nil,
            startAt: start,
            endAt: start.addingTimeInterval(3600),
            timeZone: try #require(TimeZone(identifier: "Europe/Berlin"))
        )
    }

    @Test("a created event comes back from a listing")
    func createThenList() async throws {
        let calendar = try fake()
        let created = try await calendar.create(try command())

        #expect(created.calendar.rawValue == "Privat")
        #expect(created.isAllDay == false)
        #expect(created.timeZone == "Europe/Berlin")

        let listed = try await calendar.upcoming(try ListCalendarEventsQuery())
        #expect(listed == [created])
    }

    @Test("a name that matches no calendar fails closed, never defaulting to another")
    func unknownNameFailsClosed() async throws {
        let calendar = try fake()
        await #expect(throws: ApiError.noSuchCalendar) {
            try await calendar.create(try self.command("Nope"))
        }
        await #expect(throws: ApiError.noSuchCalendar) {
            try await calendar.upcoming(try ListCalendarEventsQuery(calendars: [try calendarName("Nope")]))
        }
        #expect(await calendar.all.isEmpty, "a refused create still wrote something")
    }

    /// The divergence from the list side: two calendars wearing one name is its own error, so the
    /// message Erda relays can say "rename one" rather than "check the spelling".
    @Test("an ambiguous name is its own error, not folded into no_such_calendar")
    func ambiguousNameIsDistinct() async throws {
        let calendar = try fake(ambiguous: ["Privat"])
        await #expect(throws: ApiError.ambiguousCalendar) {
            try await calendar.create(try self.command("Privat"))
        }
        await #expect(throws: ApiError.ambiguousCalendar) {
            try await calendar.upcoming(try ListCalendarEventsQuery(calendars: [try calendarName("Privat")]))
        }
    }

    @Test("a read-only calendar exists but cannot take an event")
    func readOnlyCalendar() async throws {
        let calendar = try fake(readOnly: ["Arbeit"])
        await #expect(throws: ApiError.calendarReadOnly) {
            try await calendar.create(try self.command("Arbeit"))
        }
        // It is still readable — read-only means read-only, not invisible.
        #expect(await calendar.availableCalendars().map(\.rawValue) == ["Arbeit", "Privat"])
    }

    /// The window is the whole point of the route. An event past its end must not come back, or a
    /// caller asking "what's on today" gets next month.
    @Test("the window bounds what comes back")
    func windowBounds() async throws {
        let calendar = try fake()
        _ = try await calendar.create(try command(startingIn: 12))       // today
        _ = try await calendar.create(try command(startingIn: 24 * 10))  // ten days out

        #expect(try await calendar.upcoming(try ListCalendarEventsQuery(days: 1)).count == 1)
        #expect(try await calendar.upcoming(try ListCalendarEventsQuery(days: 7)).count == 1)
        #expect(try await calendar.upcoming(try ListCalendarEventsQuery(days: 14)).count == 2)
    }

    /// An event that started before now but has not finished is still "on" — dropping it would
    /// mean a meeting you are sitting in disappears from the answer.
    @Test("an event already in progress is still upcoming")
    func inProgressEventIsIncluded() async throws {
        let calendar = try fake()
        _ = try await calendar.create(try command(startingIn: -0.5))

        #expect(try await calendar.upcoming(try ListCalendarEventsQuery()).count == 1)
    }

    @Test("an event that has already finished is not")
    func finishedEventIsExcluded() async throws {
        let calendar = try fake()
        _ = try await calendar.create(try command(startingIn: -5))

        #expect(try await calendar.upcoming(try ListCalendarEventsQuery()).isEmpty)
    }

    @Test("omitting the calendar means every calendar, and naming one narrows to it")
    func filtering() async throws {
        let calendar = try fake()
        _ = try await calendar.create(try command("Privat"))
        _ = try await calendar.create(try command("Arbeit"))

        #expect(try await calendar.upcoming(try ListCalendarEventsQuery()).count == 2)
        let narrowed = try await calendar.upcoming(
            try ListCalendarEventsQuery(calendars: [try calendarName("Arbeit")])
        )
        #expect(narrowed.map(\.calendar.rawValue) == ["Arbeit"])
    }

    @Test("the limit truncates, soonest first")
    func limitTruncates() async throws {
        let calendar = try fake()
        for hours in [72.0, 24.0, 48.0] {
            _ = try await calendar.create(try command(startingIn: hours))
        }

        let listed = try await calendar.upcoming(try ListCalendarEventsQuery(limit: 2))
        #expect(listed.count == 2)
        #expect(listed[0].startAt < listed[1].startAt)
        #expect(listed[0].startAt == now.addingTimeInterval(24 * 3600))
    }

    @Test("unavailable access is a calendar 503, and reveals nothing")
    func unavailable() async throws {
        let calendar = try fake()
        await calendar.setAvailability(.unauthorized)

        #expect(await calendar.calendarAvailability() == .unauthorized)
        #expect(await calendar.availableCalendars().isEmpty)
        await #expect(throws: ApiError.calendarUnavailable) {
            try await calendar.upcoming(try ListCalendarEventsQuery())
        }
        await #expect(throws: ApiError.calendarUnavailable) {
            try await calendar.create(try self.command())
        }
    }
}
