import Foundation

/// Whether the Calendar back end can serve requests at all — the exact counterpart of
/// `ReminderAvailability`, and deliberately a separate value.
///
/// macOS authorizes events and reminders **independently**: a Mac can have granted one and denied
/// the other, and denying calendar access must not take the reminder routes down with it. Folding
/// both into one availability would make that impossible to express.
///
/// Anything other than `.ok` is a 503 `calendar_unavailable` — never a 500, never a stack trace.
public enum CalendarAvailability: String, Sendable, Hashable, CaseIterable, Codable {
    /// Full access granted.
    case ok
    /// `EKEventStore.authorizationStatus(for: .event)` is not `.fullAccess`.
    case unauthorized

    /// `nil` when requests may proceed.
    public var apiError: ApiError? {
        self == .ok ? nil : .calendarUnavailable
    }
}
