import Foundation

// MARK: - Wire request

/// The body of `POST /v1/reminders`, decoded strictly: unknown keys are rejected, every field is
/// capped, and `dueAt` must carry an explicit UTC offset.
///
/// **It names no list, and cannot.** The write target is pinned once, locally, in the setup window
/// (see `ListBinding`); there is no field here for a caller to choose one and no route that could
/// change the choice remotely. A client that still sends `"list"` gets a 400 from the unknown-key
/// check rather than being quietly obeyed — which is the whole reason strict decoding is there.
///
/// There is deliberately no `allDay` flag. `EKReminder` turns a date-only `dueDateComponents`
/// into an all-day reminder behind your back (`EKReminder.h`); requiring a full offset-bearing
/// timestamp means the request can only ever express a timed reminder, with no hidden mode.
public struct CreateReminderRequest: Sendable, Equatable, Decodable {
    /// Trimmed, 1…512.
    public let title: String
    /// ≤ 4096, kept verbatim (not trimmed).
    public let notes: String?
    public let dueAt: Date?
    /// 0 = none, 1 = highest … 9 = lowest.
    public let priority: Int?

    /// Note what is **not** here: `list`. It was a key once, so a stale client will send it, and
    /// that client must be told plainly rather than have its choice silently ignored.
    static let allowedKeys: Set<String> = ["title", "notes", "dueAt", "priority"]

    public init(title: String, notes: String? = nil, dueAt: Date? = nil, priority: Int? = nil) {
        self.title = title
        self.notes = notes
        self.dueAt = dueAt
        self.priority = priority
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: AnyCodingKey.self)
        try StrictDecoding.rejectUnknownKeys(in: container, allowed: Self.allowedKeys)

        self.title = try Validate.title(container.decode(String.self, forKey: "title"))
        self.notes = try container.decodeIfPresent(String.self, forKey: "notes").map(Validate.notes)
        self.dueAt = try container.decodeIfPresent(Date.self, forKey: "dueAt")
        self.priority = try container.decodeIfPresent(Int.self, forKey: "priority").map(Validate.priority)
    }

    /// Pairs the validated request with the bridge-issued id the handler minted for it.
    public func command(id: BridgeID) -> CreateReminderCommand {
        CreateReminderCommand(id: id, title: title, notes: notes, dueAt: dueAt, priority: priority ?? 0)
    }
}

// MARK: - Service-facing commands (the `RemindersService` seam, §6.2)

/// A validated create, with the bridge id already assigned so the id ↔ EventKit mapping can be
/// written in the same step as the save.
///
/// It carries **no list**: the implementation resolves the pinned write target itself (see
/// `ListBinding`), so there is no value here for a caller — or a future refactor — to steer the
/// write with. It still carries a `BridgeID`, unlike `CreateCalendarEventCommand`, because a
/// reminder has a `complete` route that needs the id ↔ EventKit mapping written at save time.
public struct CreateReminderCommand: Sendable, Equatable {
    public let id: BridgeID
    public let title: String
    public let notes: String?
    public let dueAt: Date?
    public let priority: Int

    public init(id: BridgeID, title: String, notes: String?, dueAt: Date?, priority: Int) {
        self.id = id
        self.title = title
        self.notes = notes
        self.dueAt = dueAt
        self.priority = priority
    }
}

/// `GET /v1/reminders`. Built from query parameters, not a JSON body, so it validates in its
/// initialiser instead of going through `StrictJSON`.
public struct ListRemindersQuery: Sendable, Equatable {
    /// Empty means every reminder list on this Mac.
    public let lists: [ListName]
    public let limit: Int

    public init(lists: [ListName] = [], limit: Int = Limits.listLimitDefault) throws {
        self.lists = lists
        self.limit = try Validate.listLimit(limit)
    }
}

// MARK: - Wire responses

/// One reminder as the client sees it. Carries a `BridgeID`, never an EventKit identifier, and the
/// list's name, never its `calendarIdentifier`.
public struct ReminderSnapshot: Sendable, Equatable, Codable {
    public let id: BridgeID
    public let list: ListName
    public let title: String
    public let notes: String?
    public let dueAt: Date?
    public let priority: Int
    public let isCompleted: Bool
    public let completedAt: Date?

    public init(
        id: BridgeID,
        list: ListName,
        title: String,
        notes: String? = nil,
        dueAt: Date? = nil,
        priority: Int = 0,
        isCompleted: Bool = false,
        completedAt: Date? = nil
    ) {
        self.id = id
        self.list = list
        self.title = title
        self.notes = notes
        self.dueAt = dueAt
        self.priority = priority
        self.isCompleted = isCompleted
        self.completedAt = completedAt
    }
}

/// The body of `GET /v1/reminders`.
///
/// A wrapper object rather than a bare top-level array: an array can never gain a field later
/// (a cursor, a truncation flag) without breaking every client that already parses it.
public struct ListRemindersResponse: Sendable, Equatable, Codable {
    public let items: [ReminderSnapshot]

    public init(items: [ReminderSnapshot]) {
        self.items = items
    }
}

/// The result of `POST /v1/reminders/{id}/complete`.
///
/// Completing an already-completed reminder is a **success no-op**: `EKReminder` may report
/// `isCompleted == true` with a nil `completionDate` when another client completed it, so there
/// is nothing here to assert on and nothing to fail.
public struct CompleteOutcome: Sendable, Equatable, Codable {
    public let id: BridgeID
    public let alreadyCompleted: Bool

    public init(id: BridgeID, alreadyCompleted: Bool) {
        self.id = id
        self.alreadyCompleted = alreadyCompleted
    }
}

/// What `GET /v1/status` says about the one list the bridge writes to.
///
/// The reminder counterpart of `WriteCalendarReport`, and identical in shape and reasoning. Three
/// states rather than a name plus a boolean, because "nobody has chosen one" and "the one that was
/// chosen has gone" are different problems with the same symptom, and a client that has to infer the
/// difference from a missing key will get it wrong. Both answer `503 list_not_configured` on a
/// create; only the second has a name to show.
///
/// The name is the list's *current* title when it resolves, and the title it wore at bind time when
/// it does not — which is exactly when a human needs to be told what went missing.
public struct WriteListReport: Sendable, Equatable, Codable {
    public enum State: String, Sendable, Hashable, CaseIterable, Codable {
        /// No list has ever been pinned in the setup window.
        case notConfigured = "not_configured"
        /// Pinned, and the identifier still resolves to a list on this Mac.
        case ok
        /// Pinned, but the identifier resolves to nothing right now — the list was deleted, the
        /// account was signed out, or Reminders access has been revoked (`availability`, in the
        /// same body, is what tells those apart).
        case unresolvable
    }

    public let state: State
    /// Absent only when nothing has ever been pinned.
    public let name: ListName?

    public init(state: State, name: ListName?) {
        self.state = state
        self.name = name
    }

    public static let notConfigured = WriteListReport(state: .notConfigured, name: nil)

    public static func configured(_ name: ListName) -> WriteListReport {
        WriteListReport(state: .ok, name: name)
    }

    public static func unresolvable(_ name: ListName) -> WriteListReport {
        WriteListReport(state: .unresolvable, name: name)
    }
}

/// The body of `GET /v1/status`.
///
/// The two capabilities are reported **separately** because macOS authorizes them separately: a
/// Mac can have granted reminders and denied calendars, and a single `availability` would have to
/// lie about one of them. The reminder fields keep their original names and meaning, so a client
/// written before calendars existed still reads this correctly.
public struct StatusResponse: Sendable, Equatable, Codable {
    public let availability: ReminderAvailability
    /// Every reminder list on this Mac, sorted — the names a **read** may filter by. It is not a
    /// menu to write into: `POST /v1/reminders` takes no list at all and always lands in
    /// `writeList`.
    public let lists: [ListName]
    /// The one list creates land in, and whether it still resolves. Reported here because it is the
    /// only way a caller can tell Phil *why* a create is failing with `503 list_not_configured` —
    /// and the only place the write target is visible at all, since no route can set it.
    public let writeList: WriteListReport
    public let calendarAvailability: CalendarAvailability
    /// Every calendar on this Mac, sorted — the names a **read** may filter by. It is not a menu to
    /// write into: `POST /v1/calendar-events` takes no calendar at all and always lands in
    /// `writeCalendar`.
    public let calendars: [CalendarName]
    /// The one calendar creates land in, and whether it still resolves. Reported here because it is
    /// the only way a caller can tell Phil *why* a create is failing with
    /// `503 calendar_not_configured` — and the only place the write target is visible at all, since
    /// no route can set it.
    public let writeCalendar: WriteCalendarReport

    public init(
        availability: ReminderAvailability,
        lists: [ListName],
        writeList: WriteListReport,
        calendarAvailability: CalendarAvailability,
        calendars: [CalendarName],
        writeCalendar: WriteCalendarReport
    ) {
        self.availability = availability
        self.lists = lists
        self.writeList = writeList
        self.calendarAvailability = calendarAvailability
        self.calendars = calendars
        self.writeCalendar = writeCalendar
    }
}
