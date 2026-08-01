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
    func snapshot(calendar: CalendarName) -> CalendarEventSnapshot? {
        guard let startAt, let endAt else { return nil }
        return CalendarEventSnapshot(
            calendar: calendar,
            title: title,
            notes: notes,
            startAt: startAt,
            endAt: endAt,
            isAllDay: isAllDay,
            timeZone: timeZoneIdentifier
        )
    }
}
