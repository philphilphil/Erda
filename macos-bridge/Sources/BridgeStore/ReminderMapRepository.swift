import BridgeCore
import Foundation

/// One row of `reminder_map`: the bridge-issued id and the EventKit identifier it stands for.
public struct ReminderMapEntry: Sendable, Equatable {
    public let bridgeId: BridgeID
    public let eventKitItemId: String
    /// `calendarItemExternalIdentifier`, kept for **diagnostics only**. The header enumerates
    /// four ways it can be duplicated inside one database and warns that it differs between
    /// devices for reminders on Exchange, so it is never used to resolve anything.
    public let eventKitExternalId: String?
    /// The list the reminder was in when the row was written, for diagnostics only. It resolves
    /// nothing: `complete` always re-reads the reminder's current calendar, so a stale name here
    /// can never authorise a write.
    public let listName: ListName
    public let createdAt: Date
    public let lastSeenAt: Date

    public init(
        bridgeId: BridgeID,
        eventKitItemId: String,
        eventKitExternalId: String? = nil,
        listName: ListName,
        createdAt: Date,
        lastSeenAt: Date
    ) {
        self.bridgeId = bridgeId
        self.eventKitItemId = eventKitItemId
        self.eventKitExternalId = eventKitExternalId
        self.listName = listName
        self.createdAt = createdAt
        self.lastSeenAt = lastSeenAt
    }
}

/// The `rem_<uuid>` ↔ `calendarItemIdentifier` map.
///
/// EventKit item identifiers are not sync-proof, so this table grows dangling rows after an
/// iCloud full sync. A dangling id is always a 404 — never a re-resolution attempt — and the
/// rows are pruned by a sweep that only removes entries EventKit has failed to produce for
/// several consecutive days.
public struct ReminderMapRepository: Sendable {
    private let db: SQLiteDB

    public init(db: SQLiteDB) {
        self.db = db
    }

    public func insert(_ entry: ReminderMapEntry) throws {
        try db.run(
            """
            INSERT INTO reminder_map(bridge_id, ek_item_id, ek_external_id, list_name, created_at, last_seen_at)
            VALUES (?, ?, ?, ?, ?, ?)
            """,
            [
                .text(entry.bridgeId.rawValue),
                .text(entry.eventKitItemId),
                .optionalText(entry.eventKitExternalId),
                .text(entry.listName.rawValue),
                .date(entry.createdAt),
                .date(entry.lastSeenAt),
            ]
        )
    }

    public func entry(for bridgeId: BridgeID) throws -> ReminderMapEntry? {
        try db.queryOne(selectPrefix + " WHERE bridge_id = ?", [.text(bridgeId.rawValue)], table: "reminder_map", decode: Self.decode)
    }

    public func entry(forEventKitItemId itemId: String) throws -> ReminderMapEntry? {
        try db.queryOne(selectPrefix + " WHERE ek_item_id = ?", [.text(itemId)], table: "reminder_map", decode: Self.decode)
    }

    /// Records that EventKit still resolves this id, resetting its pruning clock.
    @discardableResult
    public func touch(_ bridgeId: BridgeID, at date: Date) throws -> Bool {
        try db.run(
            "UPDATE reminder_map SET last_seen_at = ? WHERE bridge_id = ?",
            [.date(date), .text(bridgeId.rawValue)]
        ) == 1
    }

    /// Candidates for pruning: rows EventKit has not produced since `cutoff`.
    public func entriesNotSeen(since cutoff: Date) throws -> [ReminderMapEntry] {
        try db.query(
            selectPrefix + " WHERE last_seen_at < ? ORDER BY last_seen_at",
            [.date(cutoff)],
            table: "reminder_map",
            decode: Self.decode
        )
    }

    @discardableResult
    public func delete(_ bridgeId: BridgeID) throws -> Bool {
        try db.run("DELETE FROM reminder_map WHERE bridge_id = ?", [.text(bridgeId.rawValue)]) == 1
    }

    public func count() throws -> Int {
        let value = try db.queryOne("SELECT COUNT(*) FROM reminder_map", table: "reminder_map") { row in
            try row.integer(0, "count")
        }
        return Int(value ?? 0)
    }

    private let selectPrefix = """
        SELECT bridge_id, ek_item_id, ek_external_id, list_name, created_at, last_seen_at FROM reminder_map
        """

    private static func decode(_ row: SQLiteRow) throws -> ReminderMapEntry {
        guard let bridgeId = BridgeID(rawValue: try row.text(0, "bridge_id")) else {
            throw StoreError.corruptRow(table: "reminder_map", column: "bridge_id")
        }
        guard let listName = ListName(rawValue: try row.text(3, "list_name")) else {
            throw StoreError.corruptRow(table: "reminder_map", column: "list_name")
        }
        return ReminderMapEntry(
            bridgeId: bridgeId,
            eventKitItemId: try row.text(1, "ek_item_id"),
            eventKitExternalId: row.optionalText(2),
            listName: listName,
            createdAt: try row.date(4, "created_at"),
            lastSeenAt: try row.date(5, "last_seen_at")
        )
    }
}
