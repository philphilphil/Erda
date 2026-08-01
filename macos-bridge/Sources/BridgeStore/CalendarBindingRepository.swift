import BridgeCore
import Foundation

/// Where the one calendar the bridge writes to lives.
///
/// Two `meta` rows, alongside the bind address — the same table, the same posture, and for the same
/// reason: this is a choice a human makes locally, once, and **no route can change it**. The remote
/// API has nothing that touches it, exactly as it has nothing that touches the token or the
/// listener's address.
///
/// **There is deliberately no default and no auto-pick.** A missing or half-written pair reads back
/// as `nil`, and a `nil` makes `POST /v1/calendar-events` answer `503 calendar_not_configured`.
/// Falling back to `EKEventStore.defaultCalendarForNewEvents` would be worse than failing: it is
/// whatever Calendar.app last decided, it changes without anyone noticing, and the first sign of it
/// being wrong is an appointment in a shared work calendar.
public struct CalendarBindingRepository: Sendable {
    static let idKey = "calendar_id"
    static let titleKey = "calendar_title"

    private let meta: MetaRepository

    public init(meta: MetaRepository) {
        self.meta = meta
    }

    /// `nil` when no complete binding has been stored.
    ///
    /// A row with an identifier but no title — or a title that is no longer a usable
    /// `CalendarName` — is treated as absent rather than repaired: a binding that cannot be
    /// *displayed* cannot be confirmed by a human either, and an unconfirmable write target is one
    /// the bridge has no business writing to.
    public func load() throws -> CalendarBinding? {
        guard let calendarId = try meta.value(for: Self.idKey), !calendarId.isEmpty,
              let rawTitle = try meta.value(for: Self.titleKey),
              let title = CalendarName(rawValue: rawTitle)
        else { return nil }
        return CalendarBinding(calendarId: calendarId, titleAtBind: title)
    }

    /// Stores a choice verbatim. Whether the identifier still resolves is decided at every write,
    /// against EventKit — a "verified" flag on disk would be a lie with a timestamp, for the same
    /// reason `BindSettingsRepository` keeps none.
    public func save(_ binding: CalendarBinding) throws {
        try meta.set(binding.calendarId, for: Self.idKey)
        try meta.set(binding.titleAtBind.rawValue, for: Self.titleKey)
    }

    public func clear() throws {
        try meta.remove(Self.idKey)
        try meta.remove(Self.titleKey)
    }
}
