import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

/// `RawEvent` → `CalendarEventSnapshot`. The interesting cases are all about EventKit's
/// `null_unspecified` properties, which Swift imports as implicitly-unwrapped optionals that
/// really can be nil.
@Suite("Event mapping")
struct EventMappingTests {
    private let start = Date(timeIntervalSince1970: 1_785_740_400)
    /// The zone the days are derived in is always passed explicitly, so nothing here depends on
    /// the zone the test machine happens to be set to.
    private let berlin = TimeZone(identifier: "Europe/Berlin")!

    private func raw(
        calendarId: String? = "CAL-1",
        title: String = "Dentist",
        notes: String? = nil,
        startAt: Date?,
        endAt: Date?,
        isAllDay: Bool = false,
        timeZoneIdentifier: String? = "Europe/Berlin"
    ) -> RawEvent {
        RawEvent(
            calendarId: calendarId,
            title: title,
            notes: notes,
            startAt: startAt,
            endAt: endAt,
            isAllDay: isAllDay,
            timeZoneIdentifier: timeZoneIdentifier
        )
    }

    @Test("a complete event maps field for field")
    func mapsCompleteEvent() throws {
        let snapshot = try #require(
            raw(notes: "bring the referral", startAt: start, endAt: start.addingTimeInterval(3600))
                .snapshot(calendar: try calendarName("Privat"), dayZone: berlin)
        )

        #expect(snapshot.calendar.rawValue == "Privat")
        #expect(snapshot.title == "Dentist")
        #expect(snapshot.notes == "bring the referral")
        #expect(snapshot.startAt == start)
        #expect(snapshot.endAt == start.addingTimeInterval(3600))
        #expect(snapshot.isAllDay == false)
        #expect(snapshot.timeZone == "Europe/Berlin")
    }

    /// The bug this pair of fields exists for. An all-day event is *floating*: EventKit anchors it
    /// to the Mac's own zone and `EKEvent.timeZone` is nil, so the instants alone say
    /// `2026-08-10T22:00:00Z` for something Calendar.app draws on **Tuesday 11 August**. A client
    /// deriving the day from the instant reports a birthday a day early, every time, for anyone
    /// east of London.
    @Test("an all-day event carries the local calendar day, not the UTC one")
    func statesTheLocalDayOfAnAllDayEvent() throws {
        let snapshot = try #require(
            raw(
                title: "Opa’s 85th Birthday",
                // 2026-08-11 00:00:00 → 23:59:59 in Berlin.
                startAt: Date(timeIntervalSince1970: 1_786_399_200),
                endAt: Date(timeIntervalSince1970: 1_786_485_599),
                isAllDay: true,
                timeZoneIdentifier: nil
            ).snapshot(calendar: try calendarName("Geburtstage"), dayZone: berlin)
        )

        #expect(snapshot.startDay == "2026-08-11")
        // Inclusive: `endAt` is the last second of the last day, not an exclusive bound.
        #expect(snapshot.endDay == "2026-08-11")
        // The instants are untouched — the days are stated *alongside* them, not instead of them.
        #expect(snapshot.startAt == Date(timeIntervalSince1970: 1_786_399_200))
    }

    /// The mirror image, so the fix is not accidentally "+02:00 only": west of UTC the *end*
    /// instant lands on the following UTC day while the event still occupies one local day.
    @Test("a zone west of UTC gets its own local day, not the UTC one either")
    func statesTheLocalDayWestOfUTC() throws {
        let snapshot = try #require(
            raw(
                title: "Independence Day",
                // 2026-08-11 00:00:00 → 23:59:59 at -05:00; the end instant is 2026-08-12 in UTC.
                startAt: Date(timeIntervalSince1970: 1_786_424_400),
                endAt: Date(timeIntervalSince1970: 1_786_510_799),
                isAllDay: true,
                timeZoneIdentifier: nil
            ).snapshot(
                calendar: try calendarName("Privat"),
                dayZone: TimeZone(secondsFromGMT: -5 * 3600)!
            )
        )

        #expect(snapshot.startDay == "2026-08-11")
        #expect(snapshot.endDay == "2026-08-11")
    }

    /// A multi-day all-day event names both ends, and `endDay` is the last day it covers rather
    /// than the exclusive bound EventKit's instants suggest.
    @Test("a multi-day all-day event names its first and last day")
    func statesBothDaysOfAMultiDayEvent() throws {
        let snapshot = try #require(
            raw(
                title: "Urlaub",
                // 2026-08-10 00:00:00 → 2026-08-14 23:59:59 in Berlin.
                startAt: Date(timeIntervalSince1970: 1_786_312_800),
                endAt: Date(timeIntervalSince1970: 1_786_744_799),
                isAllDay: true,
                timeZoneIdentifier: nil
            ).snapshot(calendar: try calendarName("Privat"), dayZone: berlin)
        )

        #expect(snapshot.startDay == "2026-08-10")
        #expect(snapshot.endDay == "2026-08-14")
    }

    /// A timed event's day is not a fact the client needs — it has an instant and a zone, which is
    /// strictly more information — so both fields stay nil rather than being filled in "for
    /// consistency" and inviting a client to read a day off a timed appointment.
    @Test("a timed event carries no days at all")
    func timedEventsCarryNoDays() throws {
        let snapshot = try #require(
            raw(startAt: start, endAt: start.addingTimeInterval(3600))
                .snapshot(calendar: try calendarName("Privat"), dayZone: berlin)
        )

        #expect(snapshot.startDay == nil)
        #expect(snapshot.endDay == nil)
        // And a nil optional is omitted from the JSON rather than written as null.
        let encoded = String(decoding: try ResponseJSON.encode(snapshot), as: UTF8.self)
        #expect(!encoded.contains("startDay"))
        #expect(!encoded.contains("endDay"))
    }

    /// An event with no start or no end has no time to report. Reporting one anyway — `Date()`,
    /// `distantPast`, the other end — would put a fabricated appointment in front of the user.
    @Test("an event missing a start or an end is dropped rather than given a fabricated one")
    func dropsEventsWithoutTimes() throws {
        let calendar = try calendarName("Privat")
        #expect(raw(startAt: nil, endAt: start).snapshot(calendar: calendar, dayZone: berlin) == nil)
        #expect(raw(startAt: start, endAt: nil).snapshot(calendar: calendar, dayZone: berlin) == nil)
        #expect(raw(startAt: nil, endAt: nil).snapshot(calendar: calendar, dayZone: berlin) == nil)
    }

    /// An all-day event's flag has to survive: a caller told only "starts 22:00Z" would report a
    /// birthday on the wrong day for anyone east of London.
    @Test("the all-day flag survives")
    func keepsAllDayFlag() throws {
        let snapshot = try #require(
            raw(startAt: start, endAt: start.addingTimeInterval(86_400), isAllDay: true, timeZoneIdentifier: nil)
                .snapshot(calendar: try calendarName("Privat"), dayZone: berlin)
        )
        #expect(snapshot.isAllDay)
        // A floating event genuinely has no zone; inventing one would be a claim the data does
        // not make.
        #expect(snapshot.timeZone == nil)
    }

    /// An untitled event still exists and still occupies the slot, so it is reported with an empty
    /// title rather than dropped.
    @Test("an untitled event is reported, not dropped")
    func keepsUntitledEvents() throws {
        let snapshot = try #require(
            raw(title: "", startAt: start, endAt: start.addingTimeInterval(3600))
                .snapshot(calendar: try calendarName("Privat"), dayZone: berlin)
        )
        #expect(snapshot.title.isEmpty)
    }

    /// The snapshot has no `id` field at all. There is no route that takes an event id — no
    /// complete, no edit, no delete — so one would be a handle to nothing. This is the assertion
    /// that fails if somebody adds one "for symmetry" with `ReminderSnapshot`.
    @Test("a snapshot exposes no identifier of any kind")
    func exposesNoIdentifier() throws {
        let snapshot = try #require(
            raw(startAt: start, endAt: start.addingTimeInterval(3600))
                .snapshot(calendar: try calendarName("Privat"), dayZone: berlin)
        )
        let labels = Mirror(reflecting: snapshot).children.compactMap(\.label)
        #expect(
            labels == [
                "calendar", "title", "notes", "startAt", "endAt", "isAllDay", "startDay", "endDay",
                "timeZone",
            ]
        )
        // In particular, the EventKit calendar identifier the `RawEvent` carried is gone.
        let encoded = String(decoding: try ResponseJSON.encode(snapshot), as: UTF8.self)
        #expect(!encoded.contains("CAL-1"))
    }
}
