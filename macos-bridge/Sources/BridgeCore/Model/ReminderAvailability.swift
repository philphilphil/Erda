import Foundation

/// Whether the Reminders back end can serve requests at all (design dossier §3.5).
///
/// Anything other than `.ok` is a 503 `reminders_unavailable` — never a 500, never a stack trace.
public enum ReminderAvailability: String, Sendable, Hashable, CaseIterable, Codable {
    /// Full access granted and at least one healthy allowlist entry.
    case ok
    /// `EKEventStore.authorizationStatus(for: .reminder)` is not `.fullAccess`.
    case unauthorized
    /// Authorized, but no allowlist entry currently resolves — nothing may be read or written.
    case noAllowlist = "no_allowlist"

    /// `nil` when requests may proceed.
    public var apiError: ApiError? {
        self == .ok ? nil : .remindersUnavailable
    }
}
