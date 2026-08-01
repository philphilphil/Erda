import Foundation

// MARK: - Wire request

/// The body of `POST /v1/reminders`, decoded strictly: unknown keys are rejected, every field is
/// capped, and `dueAt` must carry an explicit UTC offset.
///
/// There is deliberately no `allDay` flag. `EKReminder` turns a date-only `dueDateComponents`
/// into an all-day reminder behind your back (`EKReminder.h`); requiring a full offset-bearing
/// timestamp means the request can only ever express a timed reminder, with no hidden mode.
public struct CreateReminderRequest: Sendable, Equatable, Decodable {
    public let alias: Alias
    /// Trimmed, 1…512.
    public let title: String
    /// ≤ 4096, kept verbatim (not trimmed).
    public let notes: String?
    public let dueAt: Date?
    /// 0 = none, 1 = highest … 9 = lowest.
    public let priority: Int?

    static let allowedKeys: Set<String> = ["alias", "title", "notes", "dueAt", "priority"]

    public init(alias: Alias, title: String, notes: String? = nil, dueAt: Date? = nil, priority: Int? = nil) {
        self.alias = alias
        self.title = title
        self.notes = notes
        self.dueAt = dueAt
        self.priority = priority
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: AnyCodingKey.self)
        try StrictDecoding.rejectUnknownKeys(in: container, allowed: Self.allowedKeys)

        self.alias = try container.decode(Alias.self, forKey: "alias")
        self.title = try Validate.title(container.decode(String.self, forKey: "title"))
        self.notes = try container.decodeIfPresent(String.self, forKey: "notes").map(Validate.notes)
        self.dueAt = try container.decodeIfPresent(Date.self, forKey: "dueAt")
        self.priority = try container.decodeIfPresent(Int.self, forKey: "priority").map(Validate.priority)
    }

    /// Pairs the validated request with the bridge-issued id the handler minted for it.
    public func command(id: BridgeID) -> CreateReminderCommand {
        CreateReminderCommand(id: id, alias: alias, title: title, notes: notes, dueAt: dueAt, priority: priority ?? 0)
    }
}

// MARK: - Service-facing commands (the `RemindersService` seam, §6.2)

/// A validated create, with the bridge id already assigned so the id ↔ EventKit mapping can be
/// written in the same step as the save.
public struct CreateReminderCommand: Sendable, Equatable {
    public let id: BridgeID
    public let alias: Alias
    public let title: String
    public let notes: String?
    public let dueAt: Date?
    public let priority: Int

    public init(id: BridgeID, alias: Alias, title: String, notes: String?, dueAt: Date?, priority: Int) {
        self.id = id
        self.alias = alias
        self.title = title
        self.notes = notes
        self.dueAt = dueAt
        self.priority = priority
    }
}

/// `GET /v1/reminders`. Built from query parameters, not a JSON body, so it validates in its
/// initialiser instead of going through `StrictJSON`.
public struct ListRemindersQuery: Sendable, Equatable {
    /// Empty means "every healthy allowlist entry" — resolved by the caller, never a default list.
    public let aliases: [Alias]
    public let limit: Int

    public init(aliases: [Alias] = [], limit: Int = Limits.listLimitDefault) throws {
        self.aliases = aliases
        self.limit = try Validate.listLimit(limit)
    }
}

// MARK: - Wire responses

/// One reminder as the client sees it. Carries a `BridgeID`, never an EventKit identifier.
public struct ReminderSnapshot: Sendable, Equatable, Codable {
    public let id: BridgeID
    public let alias: Alias
    public let title: String
    public let notes: String?
    public let dueAt: Date?
    public let priority: Int
    public let isCompleted: Bool
    public let completedAt: Date?

    public init(
        id: BridgeID,
        alias: Alias,
        title: String,
        notes: String? = nil,
        dueAt: Date? = nil,
        priority: Int = 0,
        isCompleted: Bool = false,
        completedAt: Date? = nil
    ) {
        self.id = id
        self.alias = alias
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
    /// Healthy aliases only; a `broken` alias is omitted and counted in `brokenAliases`.
    public let aliases: [Alias]
    public let brokenAliases: [Alias]

    public init(availability: ReminderAvailability, aliases: [Alias], brokenAliases: [Alias]) {
        self.availability = availability
        self.aliases = aliases
        self.brokenAliases = brokenAliases
    }
}
