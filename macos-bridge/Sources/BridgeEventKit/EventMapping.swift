import BridgeCore
import EventKit
import Foundation

/// A fetched event, flattened to a `Sendable` value — the event-side `RawReminder`.
///
/// **This is the isolation boundary.** `EKEvent` carries no `Sendable` annotation anywhere in the
/// EventKit headers, so it is a non-`Sendable` class under Swift 6. `init(_ event:)` is the only
/// place in this module that reads EventKit properties off an event, and a `RawEvent` is the only
/// thing allowed out of that read.
///
/// Unlike the reminder path there is no completion closure to escape from —
/// `events(matching:)` is synchronous and hands back its array on the calling thread — so the
/// compiler would not by itself stop an `EKEvent` from leaking onto a later `await`. Mapping
/// eagerly is what keeps that impossible rather than merely unlikely.
///
/// It is deliberately *not* a `CalendarEventSnapshot`: it still speaks EventKit's identifiers.
/// Turning `calendarId` into a `CalendarName` needs the live list of calendars, which is resolved
/// back on the actor.
struct RawEvent: Sendable, Equatable {
    /// `nil` when the event has no calendar. `EKCalendarItem.calendar` is `null_unspecified`,
    /// so Swift imports it as an implicitly-unwrapped optional that really can be nil.
    let calendarId: String?
    let title: String
    let notes: String?
    /// `EKEvent.startDate`/`endDate` are `null_unspecified` too. An event missing either is
    /// unusable — there is nothing to report as its time — and is dropped rather than reported
    /// with a fabricated one.
    let startAt: Date?
    let endAt: Date?
    let isAllDay: Bool
    /// The event's own zone, as an IANA identifier. Genuinely optional: a floating event has none.
    let timeZoneIdentifier: String?

    init(_ event: EKEvent) {
        self.calendarId = event.calendar?.calendarIdentifier
        // `title` is `null_unspecified`; an untitled event becomes an empty string rather than
        // being dropped, so the caller still sees that the slot is busy.
        self.title = event.title ?? ""
        self.notes = event.notes
        self.startAt = event.startDate
        self.endAt = event.endDate
        self.isAllDay = event.isAllDay
        self.timeZoneIdentifier = event.timeZone?.identifier
    }

    /// Memberwise access for tests, which have no way to build an `EKEvent`.
    init(
        calendarId: String?,
        title: String,
        notes: String? = nil,
        startAt: Date?,
        endAt: Date?,
        isAllDay: Bool = false,
        timeZoneIdentifier: String? = nil
    ) {
        self.calendarId = calendarId
        self.title = title
        self.notes = notes
        self.startAt = startAt
        self.endAt = endAt
        self.isAllDay = isAllDay
        self.timeZoneIdentifier = timeZoneIdentifier
    }

    /// The wire shape, once the caller has resolved the identifier it cannot resolve itself.
    /// `nil` for an event with no usable start or end.
    ///
    /// `dayZone` is the zone the all-day days are derived in, and it is a parameter rather than a
    /// read of `TimeZone.current` for the usual two reasons: a test may not depend on the machine
    /// it runs on, and the caller already holds this value. It has no effect on a timed event.
    func snapshot(calendar: CalendarName, dayZone: TimeZone) -> CalendarEventSnapshot? {
        guard let startAt, let endAt else { return nil }
        return CalendarEventSnapshot(
            calendar: calendar,
            title: title,
            notes: notes,
            startAt: startAt,
            endAt: endAt,
            isAllDay: isAllDay,
            // Only for an all-day event: a timed one carries its own zone, so a day would add
            // nothing and invite a client to read one off an appointment.
            startDay: isAllDay ? LocalDay.string(for: startAt, in: dayZone) : nil,
            endDay: isAllDay ? LocalDay.string(for: endAt, in: dayZone) : nil,
            timeZone: timeZoneIdentifier
        )
    }
}

/// The calendar day an instant falls on, as `yyyy-MM-dd`.
///
/// Built from Gregorian components and formatted by hand rather than through a `DateFormatter`,
/// for the same reason `DueDate.components` constructs its own calendar and never reads
/// `Calendar.current`: a formatter inherits the process's locale and calendar, so a Mac set to the
/// Buddhist calendar would emit `2569-08-11` and one set to an Arabic-numerals locale
/// `٢٠٢٦-٠٨-١١`. Neither is a date any client can parse, and neither would show up on a machine
/// configured the way ours is.
enum LocalDay {
    static func string(for date: Date, in timeZone: TimeZone) -> String {
        var gregorian = Calendar(identifier: .gregorian)
        gregorian.timeZone = timeZone
        let parts = gregorian.dateComponents([.year, .month, .day], from: date)
        // `String(format:)` with no locale argument is unlocalised, so the digits are ASCII
        // whatever the Mac's number formatting says. The zeros are unreachable — those three
        // components are always present for a `Date` — and exist only so this cannot trap.
        return String(
            format: "%04d-%02d-%02d",
            parts.year ?? 0,
            parts.month ?? 0,
            parts.day ?? 0
        )
    }
}
