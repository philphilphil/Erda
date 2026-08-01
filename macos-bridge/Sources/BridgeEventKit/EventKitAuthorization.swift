import EventKit
import Foundation

/// `EKAuthorizationStatus`, restated as a `Sendable` value with a stable wire-ish name.
///
/// It exists so `ErdaBridgeApp` can display the authorization state without importing EventKit:
/// this target is the only one that links the framework, and the setup UI is otherwise the one
/// place that would have needed a second import.
///
/// One type covers **both** entity types. `EKAuthorizationStatus` is a single enum that
/// `authorizationStatus(for:)` answers per entity, and nothing in it is reminder- or
/// event-specific; what differs is which of `RemindersAccess` / `CalendarAccess` you ask.
public enum EventKitAuthorization: String, Sendable, Hashable, CaseIterable {
    case notDetermined = "not_determined"
    case restricted
    case denied
    case fullAccess = "full_access"
    case writeOnly = "write_only"
    /// A status this build does not know about. Treated exactly like `denied`.
    case unknown

    /// **Only** `.fullAccess` is usable, for reminders and for calendars alike.
    ///
    /// Write-only access can create an item but cannot read one back. For reminders that means it
    /// can satisfy neither `list` nor `complete`. For calendars it is worse than it looks: naming
    /// a calendar by its title requires *enumerating* calendars, which write-only forbids, so
    /// write-only could not even resolve the calendar an event is supposed to go in. A bridge that
    /// accepted it would fail halfway through instead of at the door.
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

/// The reminders authorization surface, kept separate from `EventKitStore` because it has a
/// different caller and a different rule: **requesting** access is only ever triggered by a user
/// gesture in the local UI, never lazily from an HTTP handler. A request from a handler would
/// raise a TCC prompt on an unattended Mac in response to a network packet.
public enum RemindersAccess {
    /// Cheap, always current, and safe to call before any access has been granted.
    public static func status() -> EventKitAuthorization {
        EventKitAuthorization(EKEventStore.authorizationStatus(for: .reminder))
    }

    /// The macOS 14+ API. The deprecated `requestAccess(to:completion:)` is never used: on a
    /// macOS 26 system it maps onto the same TCC surface but with worse semantics, and it is
    /// the call that produces "Authorized" instead of the `.fullAccess`/`.writeOnly` split.
    ///
    /// - Important: call this from a user gesture only.
    public static func requestFullAccess() async -> Result<EventKitAuthorization, any Error> {
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
    /// This is a **local** readout, for the setup UI: it carries the `calendarIdentifier` and the
    /// source account, neither of which ever reaches the wire. (A remote caller does learn the
    /// *names* of the lists, from `GET /v1/status` — it has to, since a name is how it addresses
    /// one.) Returns an empty array when access is not usable rather than guessing.
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

/// The calendar authorization surface. Deliberately the mirror image of `RemindersAccess`,
/// including the rule that a request only ever comes from a user gesture.
///
/// **The two are independent.** macOS keeps a separate TCC record per entity type, so denying one
/// says nothing about the other, and neither of these statuses may be derived from the other.
public enum CalendarAccess {
    /// Cheap, always current, and safe to call before any access has been granted.
    public static func status() -> EventKitAuthorization {
        EventKitAuthorization(EKEventStore.authorizationStatus(for: .event))
    }

    /// The macOS 14+ API, never the deprecated `requestAccess(to:completion:)`.
    ///
    /// Full access rather than write-only is a decision with a cost, taken deliberately: naming a
    /// calendar by its title means enumerating calendars, which write-only forbids. See
    /// `macos-bridge/README.md`'s threat model — the bridge can read every calendar event on this
    /// Mac, and that is the price of addressing a calendar by name.
    ///
    /// - Important: call this from a user gesture only.
    public static func requestFullAccess() async -> Result<EventKitAuthorization, any Error> {
        let store = EKEventStore()
        do {
            _ = try await store.requestFullAccessToEvents()
            return .success(status())
        } catch {
            return .failure(error)
        }
    }

    /// The calendars visible right now, flattened to `Sendable` values. A **local** readout for
    /// the setup UI: it carries the `calendarIdentifier`, which never reaches the wire.
    public static func calendars() -> [CalendarInfo] {
        guard status().isUsable else { return [] }
        return EKEventStore().calendars(for: .event).map(CalendarInfo.init)
    }

    /// How many calendars are visible right now — a "did the grant actually take" readout.
    public static func calendarCount() -> Int {
        calendars().count
    }
}

/// One Reminders list as the local UI sees it. Carries the `calendarIdentifier` so the UI can key
/// rows on something stable; it never reaches the wire.
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

/// One calendar as the local UI sees it — the event-side counterpart of `ReminderListInfo`.
public struct CalendarInfo: Sendable, Equatable, Hashable {
    public let calendarId: String
    public let title: String
    /// The account the calendar belongs to ("iCloud", "Other", …), so a human can tell two
    /// same-named calendars apart — which is exactly the case the bridge refuses to guess at.
    public let source: String
    /// A subscribed or holiday calendar is visible but cannot take an event, which is by far the
    /// most likely reason a create fails against a name that resolved perfectly well.
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
        self.source = calendar.source?.title ?? "unknown"
        self.isWritable = calendar.allowsContentModifications
            && calendar.allowedEntityTypes.contains(.event)
    }
}
