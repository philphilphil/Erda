import Foundation

/// Whether the Reminders back end can serve requests at all (design dossier §3.5).
///
/// Anything other than `.ok` is a 503 `reminders_unavailable` — never a 500, never a stack trace.
///
/// There used to be a third case, `no_allowlist`: authorized, but with no usable binding. Removing
/// the allowlist removed the state — authorization is now the only thing that can stand between
/// the bridge and the Reminders database.
public enum ReminderAvailability: String, Sendable, Hashable, CaseIterable, Codable {
    /// Full access granted.
    case ok
    /// `EKEventStore.authorizationStatus(for: .reminder)` is not `.fullAccess`.
    case unauthorized

    /// `nil` when requests may proceed.
    public var apiError: ApiError? {
        self == .ok ? nil : .remindersUnavailable
    }
}
