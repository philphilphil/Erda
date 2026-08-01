import Foundation

/// The **one** calendar the bridge writes to, as a human pinned it in the setup window.
///
/// Writes are pinned to a single calendar and reads are not: `POST /v1/calendar-events` carries no
/// calendar at all, while `GET /v1/calendar-events` still spans every calendar unless it names one.
/// That asymmetry is the whole point — Erda cannot choose where an appointment lands, cannot be
/// talked into choosing, and never needs to know a calendar's name to create one.
///
/// It carries **both** halves for the reason `EKCalendar.h` states outright: a
/// `calendarIdentifier` is not sync-proof. The identifier is the authority — resolution is by
/// identifier and only by identifier, so renaming the calendar in Calendar.app changes nothing —
/// and the title is what a human is shown when the identifier stops resolving. Re-binding by title
/// is **never** automatic: a title is exactly the thing another account can also be wearing, and a
/// silent re-bind would move Phil's appointments into somebody else's calendar. A human confirms a
/// re-bind, in the setup window, or there is no write at all.
public struct CalendarBinding: Sendable, Equatable, Hashable {
    /// `EKCalendar.calendarIdentifier`. Local to this Mac and never on the wire — a caller learns
    /// the calendar's *name* from `GET /v1/status` and nothing more.
    public let calendarId: String
    /// The title the calendar wore when it was pinned. Diagnostic, never a resolution key.
    public let titleAtBind: CalendarName

    public init(calendarId: String, titleAtBind: CalendarName) {
        self.calendarId = calendarId
        self.titleAtBind = titleAtBind
    }
}
