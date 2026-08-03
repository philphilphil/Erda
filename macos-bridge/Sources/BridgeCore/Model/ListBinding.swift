import Foundation

/// The **one** reminder list the bridge writes to, as a human pinned it in the setup window.
///
/// It is the reminder counterpart of `CalendarBinding`, and exists for the same reason: writes are
/// pinned to a single list and reads are not. `POST /v1/reminders` carries no list at all, while
/// `GET /v1/reminders` still spans every list unless it names one. That asymmetry is the whole point
/// — Erda cannot choose which list a task lands in, cannot be talked into choosing, and never needs
/// to know a list's name to create one.
///
/// It carries **both** halves for the reason `EKCalendar.h` states outright: a `calendarIdentifier`
/// is not sync-proof. The identifier is the authority — resolution is by identifier and only by
/// identifier, so renaming the list in Reminders.app changes nothing — and the title is what a human
/// is shown when the identifier stops resolving. Re-binding by title is **never** automatic: a title
/// is exactly the thing another account can also be wearing, and a silent re-bind would move Phil's
/// tasks into somebody else's list. A human confirms a re-bind, in the setup window, or there is no
/// write at all.
public struct ListBinding: Sendable, Equatable, Hashable {
    /// `EKCalendar.calendarIdentifier`. Local to this Mac and never on the wire — a caller learns
    /// the list's *name* from `GET /v1/status` and nothing more.
    public let listId: String
    /// The title the list wore when it was pinned. Diagnostic, never a resolution key.
    public let titleAtBind: ListName

    public init(listId: String, titleAtBind: ListName) {
        self.listId = listId
        self.titleAtBind = titleAtBind
    }
}
