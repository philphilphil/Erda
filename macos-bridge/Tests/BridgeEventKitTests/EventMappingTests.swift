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
                .snapshot(calendar: try calendarName("Privat"))
        )

        #expect(snapshot.calendar.rawValue == "Privat")
        #expect(snapshot.title == "Dentist")
        #expect(snapshot.notes == "bring the referral")
        #expect(snapshot.startAt == start)
        #expect(snapshot.endAt == start.addingTimeInterval(3600))
        #expect(snapshot.isAllDay == false)
        #expect(snapshot.timeZone == "Europe/Berlin")
    }

    /// An event with no start or no end has no time to report. Reporting one anyway — `Date()`,
    /// `distantPast`, the other end — would put a fabricated appointment in front of the user.
    @Test("an event missing a start or an end is dropped rather than given a fabricated one")
    func dropsEventsWithoutTimes() throws {
        let calendar = try calendarName("Privat")
        #expect(raw(startAt: nil, endAt: start).snapshot(calendar: calendar) == nil)
        #expect(raw(startAt: start, endAt: nil).snapshot(calendar: calendar) == nil)
        #expect(raw(startAt: nil, endAt: nil).snapshot(calendar: calendar) == nil)
    }

    /// An all-day event's flag has to survive: a caller told only "starts 22:00Z" would report a
    /// birthday on the wrong day for anyone east of London.
    @Test("the all-day flag survives")
    func keepsAllDayFlag() throws {
        let snapshot = try #require(
            raw(startAt: start, endAt: start.addingTimeInterval(86_400), isAllDay: true, timeZoneIdentifier: nil)
                .snapshot(calendar: try calendarName("Privat"))
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
                .snapshot(calendar: try calendarName("Privat"))
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
                .snapshot(calendar: try calendarName("Privat"))
        )
        let labels = Mirror(reflecting: snapshot).children.compactMap(\.label)
        #expect(labels == ["calendar", "title", "notes", "startAt", "endAt", "isAllDay", "timeZone"])
        // In particular, the EventKit calendar identifier the `RawEvent` carried is gone.
        let encoded = String(decoding: try ResponseJSON.encode(snapshot), as: UTF8.self)
        #expect(!encoded.contains("CAL-1"))
    }
}
