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
    /// Covers TCC's propagation delay — see `GrantPropagation` for the log that motivated it.
    ///
    /// It is **static**, which is the point: the setup window and the HTTP surface both read
    /// `status()`, so they cannot disagree. A window claiming "granted" while `GET /v1/status` says
    /// `unauthorized` would be the same bug wearing a different hat.
    private static let grant = GrantNote()

    /// Cheap, always current, and safe to call before any access has been granted.
    ///
    /// The **one** source of truth for this grant, for the UI and the request path alike.
    public static func status() -> EventKitAuthorization {
        let reported = EventKitAuthorization(EKEventStore.authorizationStatus(for: .reminder))
        // Once tccd has answered, the note has no further job. Dropping it here keeps the override
        // window as short as it can be rather than as long as it is allowed to be.
        if reported != .notDetermined { grant.clear() }
        return GrantPropagation.resolve(reported: reported, grantedRecently: grant.isActive())
    }

    /// The macOS 14+ API. The deprecated `requestAccess(to:completion:)` is never used: on a
    /// macOS 26 system it maps onto the same TCC surface but with worse semantics, and it is
    /// the call that produces "Authorized" instead of the `.fullAccess`/`.writeOnly` split.
    ///
    /// - Important: call this from a user gesture only.
    ///
    /// The store is parked in `pendingStore` for the duration of the call — the same reason
    /// `CalendarAccess` does it, and no less true here: a plain local `let` has its last use at the
    /// call itself, so ARC may release it while the TCC prompt is still on screen and take the
    /// prompt down with the abandoned request.
    @MainActor
    public static func requestFullAccess() async -> Result<EventKitAuthorization, any Error> {
        let store = EKEventStore()
        pendingStore = store
        defer { pendingStore = nil }

        AuthorizationLog.write("reminders: requesting full access (status before: \(status()))")
        do {
            let granted = try await store.requestFullAccessToReminders()
            if granted { grant.record() }
            // The returned `granted` flag does not replace re-reading the status: the two can
            // disagree when the user answers the prompt and then changes their mind in System
            // Settings before this continuation resumes. But a *single* read on the next line races
            // tccd — see `GrantPropagation` — so the read is repeated until it settles.
            _ = await GrantSettling.settle {
                EventKitAuthorization(EKEventStore.authorizationStatus(for: .reminder))
            }
            AuthorizationLog.write("reminders: request returned granted=\(granted), status now \(status())")
            return .success(status())
        } catch {
            AuthorizationLog.write("reminders: request threw \(error)")
            return .failure(error)
        }
    }

    @MainActor private static var pendingStore: EKEventStore?

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
    /// The calendar half of the same note. Separate from the reminder one because the two TCC
    /// records are separate: granting calendars must not make the bridge claim reminders too.
    private static let grant = GrantNote()

    /// Cheap, always current, and safe to call before any access has been granted.
    ///
    /// The **one** source of truth for this grant. `EventKitStore.calendarAvailability()` — and so
    /// `GET /v1/status` — reads exactly this, as does the setup window, so the two can never
    /// disagree about whether access is usable.
    public static func status() -> EventKitAuthorization {
        let reported = EventKitAuthorization(EKEventStore.authorizationStatus(for: .event))
        if reported != .notDetermined { grant.clear() }
        return GrantPropagation.resolve(reported: reported, grantedRecently: grant.isActive())
    }

    /// The macOS 14+ API, never the deprecated `requestAccess(to:completion:)`.
    ///
    /// Full access rather than write-only is a decision with a cost, taken deliberately: naming a
    /// calendar by its title means enumerating calendars, which write-only forbids. See
    /// `macos-bridge/README.md`'s threat model — the bridge can read every calendar event on this
    /// Mac, and that is the price of addressing a calendar by name.
    ///
    /// - Important: call this from a user gesture only.
    ///
    /// The store is parked in `pendingStore` for the duration of the call. A plain local `let` is
    /// not enough: its last use is the call itself, so ARC is free to release it the moment the
    /// request is in flight — while the TCC prompt is still on screen — and the abandoned request
    /// can take the prompt with it.
    @MainActor
    public static func requestFullAccess() async -> Result<EventKitAuthorization, any Error> {
        let store = EKEventStore()
        pendingStore = store
        defer { pendingStore = nil }

        AuthorizationLog.write("calendar: requesting full access (status before: \(status()))")
        do {
            let granted = try await store.requestFullAccessToEvents()
            if granted { grant.record() }
            // A single read here is what produced "granted=true, status now notDetermined" and left
            // the bridge reporting `unauthorized` until it was relaunched. Re-read until it settles;
            // `GrantNote` covers a tccd slower than the budget. See `GrantPropagation`.
            _ = await GrantSettling.settle {
                EventKitAuthorization(EKEventStore.authorizationStatus(for: .event))
            }
            AuthorizationLog.write("calendar: request returned granted=\(granted), status now \(status())")
            return .success(status())
        } catch {
            AuthorizationLog.write("calendar: request threw \(error)")
            return .failure(error)
        }
    }

    @MainActor private static var pendingStore: EKEventStore?

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

/// A deliberately dumb append-only log for authorization attempts.
///
/// Diagnostic, not audit: the audit log is content-free by construction and must stay that way, so
/// permission-flow noise does not belong in it. This records only statuses and errors — never
/// reminder or event content — and lives beside the other local logs.
public enum AuthorizationLog {
    private static let lock = NSLock()

    public static func write(_ message: String) {
        let line = "\(ISO8601DateFormatter().string(from: Date())) \(message)\n"
        guard let data = line.data(using: .utf8) else { return }
        let directory = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/ErdaBridge", isDirectory: true)
        let url = directory.appendingPathComponent("authorization.log")

        lock.lock()
        defer { lock.unlock() }
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            try? data.write(to: url)
        }
    }
}
