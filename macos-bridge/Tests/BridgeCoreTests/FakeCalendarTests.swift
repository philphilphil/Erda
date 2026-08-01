import Foundation
import Testing

@testable import BridgeCore

/// `FakeCalendar` is what the whole HTTP surface is exercised against, so its *contract* has to
/// match the real one — a fake that fails differently would make those tests worthless. These pin
/// the behaviours that matter: writes go to the pinned calendar and nowhere else, an unpinned or
/// vanished target fails closed rather than defaulting, a read filter still fails closed on an
/// unknown name and *differently* on an ambiguous one, a read-only calendar cannot take an event,
/// and the window bounds what comes back.
@Suite("FakeCalendar")
struct FakeCalendarTests {
    private let now = Date(timeIntervalSince1970: 1_780_000_000)

    private func fake(
        calendars: [String] = ["Privat", "Arbeit"],
        writeCalendar: String? = "Privat",
        readOnly: [String] = [],
        ambiguous: [String] = []
    ) throws -> FakeCalendar {
        FakeCalendar(
            calendars: Set(try calendars.map { try calendarName($0) }),
            writeCalendar: try writeCalendar.map { try calendarName($0) },
            readOnly: Set(try readOnly.map { try calendarName($0) }),
            ambiguous: Set(try ambiguous.map { try calendarName($0) }),
            clock: ManualClock(now: now)
        )
    }

    private func command(
        title: String = "Dentist",
        startingIn hours: Double = 24
    ) throws -> CreateCalendarEventCommand {
        let start = now.addingTimeInterval(hours * 3600)
        return CreateCalendarEventCommand(
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

    /// The write target is not something a create can steer, so this is the assertion that it lands
    /// where it was pinned — including when that is *not* the alphabetically first calendar, which
    /// is what a lazy implementation would pick.
    @Test("a create lands in the pinned calendar, whatever else this Mac has")
    func createUsesThePinnedCalendar() async throws {
        let calendar = try fake(writeCalendar: "Arbeit")
        let created = try await calendar.create(try command())

        #expect(created.calendar.rawValue == "Arbeit")
        #expect(await calendar.all.map(\.calendar.rawValue) == ["Arbeit"])
    }

    /// Nothing pinned. There are two perfectly good writable calendars sitting right there, and the
    /// answer is still no — a default here is exactly the guess this design exists to remove.
    @Test("with no calendar pinned, a create fails closed rather than picking one")
    func unpinnedFailsClosed() async throws {
        let calendar = try fake(writeCalendar: nil)
        await #expect(throws: ApiError.calendarNotConfigured) {
            try await calendar.create(try self.command())
        }
        #expect(await calendar.all.isEmpty, "a refused create still wrote something")
        // Reads are untouched: the narrowing is on writes only.
        #expect(try await calendar.upcoming(try ListCalendarEventsQuery()).isEmpty)
        #expect(await calendar.availableCalendars().map(\.rawValue) == ["Arbeit", "Privat"])
    }

    /// Pinned, then deleted in Calendar.app. The same refusal as never having pinned one — and
    /// emphatically not a re-bind onto whatever else is around.
    @Test("a pinned calendar that has gone fails closed rather than re-binding")
    func vanishedTargetFailsClosed() async throws {
        let calendar = try fake()
        await calendar.forget(try calendarName("Privat"))

        await #expect(throws: ApiError.calendarNotConfigured) {
            try await calendar.create(try self.command())
        }
        #expect(await calendar.all.isEmpty)
    }

    /// The status readout has to tell the two apart even though the create does not.
    @Test("the write-calendar readout distinguishes never-chosen from gone")
    func writeCalendarReport() async throws {
        #expect(await (try fake(writeCalendar: nil)).writeCalendar() == .notConfigured)
        #expect(await (try fake()).writeCalendar() == .configured(try calendarName("Privat")))

        let vanished = try fake()
        await vanished.forget(try calendarName("Privat"))
        #expect(await vanished.writeCalendar() == .unresolvable(try calendarName("Privat")))
    }

    @Test("a read filter naming no calendar fails closed, never widening to all of them")
    func unknownNameFailsClosed() async throws {
        let calendar = try fake()
        await #expect(throws: ApiError.noSuchCalendar) {
            try await calendar.upcoming(try ListCalendarEventsQuery(calendars: [try calendarName("Nope")]))
        }
    }

    /// The divergence from the list side: two calendars wearing one name is its own error, so the
    /// message Erda relays can say "rename one" rather than "check the spelling". It bears on reads
    /// only now — a write resolves an identifier, which cannot be ambiguous.
    @Test("an ambiguous read filter is its own error, not folded into no_such_calendar")
    func ambiguousNameIsDistinct() async throws {
        let calendar = try fake(ambiguous: ["Privat"])
        await #expect(throws: ApiError.ambiguousCalendar) {
            try await calendar.upcoming(try ListCalendarEventsQuery(calendars: [try calendarName("Privat")]))
        }
    }

    @Test("a read-only calendar exists but cannot take an event")
    func readOnlyCalendar() async throws {
        let calendar = try fake(writeCalendar: "Arbeit", readOnly: ["Arbeit"])
        await #expect(throws: ApiError.calendarReadOnly) {
            try await calendar.create(try self.command())
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
        _ = try await calendar.create(try command())
        // Re-pinned between the two creates, because that is now the *only* way an event reaches a
        // second calendar — which is exactly the asymmetry being tested: reads span both.
        await calendar.setWriteCalendar(try calendarName("Arbeit"))
        _ = try await calendar.create(try command())

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
