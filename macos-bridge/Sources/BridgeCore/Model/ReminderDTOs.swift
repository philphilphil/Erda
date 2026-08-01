import Foundation

// MARK: - Wire request

/// The body of `POST /v1/reminders`, decoded strictly: unknown keys are rejected, every field is
/// capped, and `dueAt` must carry an explicit UTC offset.
///
/// There is deliberately no `allDay` flag. `EKReminder` turns a date-only `dueDateComponents`
/// into an all-day reminder behind your back (`EKReminder.h`); requiring a full offset-bearing
/// timestamp means the request can only ever express a timed reminder, with no hidden mode.
public struct CreateReminderRequest: Sendable, Equatable, Decodable {
    /// The list's name as it reads in Reminders.app. Required: there is no default list, and a
    /// name that matches nothing fails rather than landing somewhere plausible.
    public let list: ListName
    /// Trimmed, 1…512.
    public let title: String
    /// ≤ 4096, kept verbatim (not trimmed).
    public let notes: String?
    public let dueAt: Date?
    /// 0 = none, 1 = highest … 9 = lowest.
    public let priority: Int?

    static let allowedKeys: Set<String> = ["list", "title", "notes", "dueAt", "priority"]

    public init(list: ListName, title: String, notes: String? = nil, dueAt: Date? = nil, priority: Int? = nil) {
        self.list = list
        self.title = title
        self.notes = notes
        self.dueAt = dueAt
        self.priority = priority
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: AnyCodingKey.self)
        try StrictDecoding.rejectUnknownKeys(in: container, allowed: Self.allowedKeys)

        self.list = try container.decode(ListName.self, forKey: "list")
        self.title = try Validate.title(container.decode(String.self, forKey: "title"))
        self.notes = try container.decodeIfPresent(String.self, forKey: "notes").map(Validate.notes)
        self.dueAt = try container.decodeIfPresent(Date.self, forKey: "dueAt")
        self.priority = try container.decodeIfPresent(Int.self, forKey: "priority").map(Validate.priority)
    }

    /// Pairs the validated request with the bridge-issued id the handler minted for it.
    public func command(id: BridgeID) -> CreateReminderCommand {
        CreateReminderCommand(id: id, list: list, title: title, notes: notes, dueAt: dueAt, priority: priority ?? 0)
    }
}

// MARK: - Service-facing commands (the `RemindersService` seam, §6.2)

/// A validated create, with the bridge id already assigned so the id ↔ EventKit mapping can be
/// written in the same step as the save.
public struct CreateReminderCommand: Sendable, Equatable {
    public let id: BridgeID
    public let list: ListName
    public let title: String
    public let notes: String?
    public let dueAt: Date?
    public let priority: Int

    public init(id: BridgeID, list: ListName, title: String, notes: String?, dueAt: Date?, priority: Int) {
        self.id = id
        self.list = list
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

/// The body of `GET /v1/status`.
public struct StatusResponse: Sendable, Equatable, Codable {
    public let availability: ReminderAvailability
    /// Every reminder list on this Mac, sorted — the names a caller may address. Empty when access
    /// is not usable, which is exactly what `availability` is there to explain.
    public let lists: [ListName]

    public init(availability: ReminderAvailability, lists: [ListName]) {
        self.availability = availability
        self.lists = lists
    }
}
