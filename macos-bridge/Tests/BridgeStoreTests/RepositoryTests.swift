import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

@Suite("Reminder map repository")
struct ReminderMapRepositoryTests {
    private let root = TemporaryRoot()

    private func entry(
        bridgeId: BridgeID = BridgeID.generate(),
        itemId: String = "EK-1",
        externalId: String? = "EXT-1",
        list: String = "Groceries",
        createdAt: Date = Date(timeIntervalSince1970: 1_780_000_000),
        lastSeenAt: Date = Date(timeIntervalSince1970: 1_780_000_000)
    ) throws -> ReminderMapEntry {
        ReminderMapEntry(
            bridgeId: bridgeId,
            eventKitItemId: itemId,
            eventKitExternalId: externalId,
            listName: try listName(list),
            createdAt: createdAt,
            lastSeenAt: lastSeenAt
        )
    }

    @Test("entries round-trip and are findable from either side")
    func roundTrips() throws {
        let store = try root.open()
        let mapped = try entry()
        try store.reminderMap.insert(mapped)

        #expect(try store.reminderMap.entry(for: mapped.bridgeId) == mapped)
        #expect(try store.reminderMap.entry(forEventKitItemId: "EK-1") == mapped)
        #expect(try store.reminderMap.count() == 1)
    }

    @Test("a missing external identifier is allowed — it is diagnostics only")
    func allowsMissingExternalId() throws {
        let store = try root.open()
        let mapped = try entry(externalId: nil)
        try store.reminderMap.insert(mapped)
        #expect(try store.reminderMap.entry(for: mapped.bridgeId)?.eventKitExternalId == nil)
    }

    @Test("one EventKit item cannot be mapped twice")
    func enforcesUniqueItemId() throws {
        let store = try root.open()
        try store.reminderMap.insert(try entry(itemId: "EK-1"))

        #expect(throws: (any Error).self) {
            try store.reminderMap.insert(try self.entry(itemId: "EK-1"))
        }
        #expect(try store.reminderMap.count() == 1)
    }

    @Test("an unknown id is nil, never a guess")
    func unknownIdIsNil() throws {
        let store = try root.open()
        #expect(try store.reminderMap.entry(for: BridgeID.generate()) == nil)
        #expect(try store.reminderMap.entry(forEventKitItemId: "EK-nope") == nil)
    }

    @Test("touching resets the pruning clock")
    func touchUpdatesLastSeen() throws {
        let store = try root.open()
        let mapped = try entry()
        try store.reminderMap.insert(mapped)

        let later = Date(timeIntervalSince1970: 1_780_090_000)
        #expect(try store.reminderMap.touch(mapped.bridgeId, at: later))
        #expect(try store.reminderMap.entry(for: mapped.bridgeId)?.lastSeenAt == later)
        #expect(try store.reminderMap.touch(BridgeID.generate(), at: later) == false)
    }

    @Test("stale rows are listed for pruning, fresh ones are not")
    func listsStaleRows() throws {
        let store = try root.open()
        let stale = try entry(itemId: "EK-stale", lastSeenAt: Date(timeIntervalSince1970: 1_770_000_000))
        let fresh = try entry(itemId: "EK-fresh", lastSeenAt: Date(timeIntervalSince1970: 1_790_000_000))
        try store.reminderMap.insert(stale)
        try store.reminderMap.insert(fresh)

        let cutoff = Date(timeIntervalSince1970: 1_780_000_000)
        let candidates = try store.reminderMap.entriesNotSeen(since: cutoff)
        #expect(candidates.map(\.eventKitItemId) == ["EK-stale"])
    }

    @Test("deletion is reported honestly")
    func deletes() throws {
        let store = try root.open()
        let mapped = try entry()
        try store.reminderMap.insert(mapped)

        #expect(try store.reminderMap.delete(mapped.bridgeId))
        #expect(try store.reminderMap.delete(mapped.bridgeId) == false)
        #expect(try store.reminderMap.count() == 0)
    }

    /// A list name is recorded for diagnostics, and it is still a validated type — a row someone
    /// hand-edited into something unloggable fails closed rather than being read back.
    @Test("a hand-edited list name fails closed")
    func rejectsCorruptListName() throws {
        let store = try root.open()
        try store.db.run(
            """
            INSERT INTO reminder_map(bridge_id, ek_item_id, ek_external_id, list_name, created_at, last_seen_at)
            VALUES (?, 'EK-1', NULL, '', 0, 0)
            """,
            [.text(BridgeID.generate().rawValue)]
        )
        #expect(throws: StoreError.corruptRow(table: "reminder_map", column: "list_name")) {
            try store.reminderMap.entry(forEventKitItemId: "EK-1")
        }
    }

    @Test("a hand-edited bridge id fails closed")
    func rejectsCorruptRows() throws {
        let store = try root.open()
        try store.db.run(
            """
            INSERT INTO reminder_map(bridge_id, ek_item_id, ek_external_id, list_name, created_at, last_seen_at)
            VALUES ('not-a-bridge-id', 'EK-1', NULL, 'Groceries', 0, 0)
            """
        )
        #expect(throws: StoreError.corruptRow(table: "reminder_map", column: "bridge_id")) {
            try store.reminderMap.entry(forEventKitItemId: "EK-1")
        }
    }
}
