import EventKit
import Foundation

/// `EKAuthorizationStatus`, restated as a `Sendable` value with a stable wire-ish name.
///
/// It exists so `ErdaBridgeApp` can display the authorization state without importing EventKit:
/// this target is the only one that links the framework, and the setup UI is otherwise the one
/// place that would have needed a second import.
public enum RemindersAuthorization: String, Sendable, Hashable, CaseIterable {
    case notDetermined = "not_determined"
    case restricted
    case denied
    case fullAccess = "full_access"
    case writeOnly = "write_only"
    /// A status this build does not know about. Treated exactly like `denied`.
    case unknown

    /// **Only** `.fullAccess` is usable. Write-only access can create a reminder but cannot read
    /// one back, so it can satisfy neither `list` nor `complete` — a bridge that accepted it
    /// would fail halfway through instead of at the door.
    public var isUsable: Bool { self == .fullAccess }

    public var displayText: String {
        switch self {
        case .notDetermined: "not determined"
        case .restricted: "restricted"
        case .denied: "denied"
        case .fullAccess: "full access"
        case .writeOnly: "write only"
        case .unknown: "unknown"
        }
    }

    init(_ status: EKAuthorizationStatus) {
        switch status {
        case .notDetermined: self = .notDetermined
        case .restricted: self = .restricted
        case .denied: self = .denied
        case .fullAccess: self = .fullAccess
        case .writeOnly: self = .writeOnly
        @unknown default: self = .unknown
        }
    }
}

/// The authorization surface, kept separate from `EventKitReminders` because it has a different
/// caller and a different rule: **requesting** access is only ever triggered by a user gesture in
/// the local UI, never lazily from an HTTP handler. A request from a handler would raise a TCC
/// prompt on an unattended Mac in response to a network packet.
public enum RemindersAccess {
    /// Cheap, always current, and safe to call before any access has been granted.
    public static func status() -> RemindersAuthorization {
        RemindersAuthorization(EKEventStore.authorizationStatus(for: .reminder))
    }

    /// The macOS 14+ API. The deprecated `requestAccess(to:completion:)` is never used: on a
    /// macOS 26 system it maps onto the same TCC surface but with worse semantics, and it is
    /// the call that produces "Authorized" instead of the `.fullAccess`/`.writeOnly` split.
    ///
    /// - Important: call this from a user gesture only.
    public static func requestFullAccess() async -> Result<RemindersAuthorization, any Error> {
        let store = EKEventStore()
        do {
            _ = try await store.requestFullAccessToReminders()
            // The returned `granted` flag is ignored in favour of re-reading the status: they can
            // disagree when the user answers the prompt and then changes their mind in System
            // Settings before this continuation resumes.
            return .success(status())
        } catch {
            return .failure(error)
        }
    }

    /// The Reminders lists visible right now, flattened to `Sendable` values.
    ///
    /// This is a **local** readout: it is what the setup UI binds an alias against, and it has no
    /// route from the HTTP API — a remote caller can never enumerate the lists on this Mac.
    /// Returns an empty array when access is not usable rather than guessing.
    public static func lists() -> [ReminderListInfo] {
        guard status().isUsable else { return [] }
        return EKEventStore().calendars(for: .reminder).map(ReminderListInfo.init)
    }

    /// How many Reminders lists are visible right now — a "did the grant actually take" readout
    /// for the setup UI.
    public static func reminderListCount() -> Int {
        lists().count
    }
}

/// One Reminders list as the local UI sees it. Carries the `calendarIdentifier` because binding
/// an alias is exactly the act of writing that identifier down; it never reaches the wire.
public struct ReminderListInfo: Sendable, Equatable, Hashable {
    public let calendarId: String
    public let title: String
    /// The account the list belongs to ("iCloud", "On My Mac", …), recorded alongside the
    /// binding so a human can tell two same-named lists apart.
    public let source: String
    public let isWritable: Bool

    public init(calendarId: String, title: String, source: String, isWritable: Bool) {
        self.calendarId = calendarId
        self.title = title
        self.source = source
        self.isWritable = isWritable
    }

    init(_ calendar: EKCalendar) {
        self.calendarId = calendar.calendarIdentifier
        self.title = calendar.title
        // `source` is `null_unspecified`, so it really can be nil despite the Swift type.
        self.source = calendar.source?.title ?? "unknown"
        self.isWritable = calendar.allowsContentModifications
            && calendar.allowedEntityTypes.contains(.reminder)
    }
}
