import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

@Suite("Allowlist repository")
struct AllowlistRepositoryTests {
    private let root = TemporaryRoot()

    @Test("entries round-trip with every field intact")
    func roundTrips() throws {
        let store = try root.open()
        let entry = try allowlistEntry("inbox")
        try store.allowlist.upsert(entry)

        let loaded = try #require(try store.allowlist.entry(for: entry.alias))
        #expect(loaded == entry)
    }

    @Test("upsert re-binds an existing alias instead of duplicating it")
    func upsertRebinds() throws {
        let store = try root.open()
        try store.allowlist.upsert(try allowlistEntry("inbox", calendarId: "cal-old"))
        try store.allowlist.upsert(try allowlistEntry("inbox", calendarId: "cal-new"))

        let all = try store.allowlist.all()
        #expect(all.count == 1)
        #expect(all.first?.calendarId == "cal-new")
    }

    @Test("the loaded table becomes the BridgeCore resolver, still failing closed")
    func loadsIntoResolver() throws {
        let store = try root.open()
        try store.allowlist.upsert(try allowlistEntry("inbox"))
        try store.allowlist.upsert(try allowlistEntry("gone", state: .broken))

        let resolver = try store.allowlist.load()
        #expect(resolver.healthyAliases == [try alias("inbox")])
        #expect(resolver.brokenAliases == [try alias("gone")])
        #expect(throws: ApiError.aliasBroken) { try resolver.resolve(try alias("gone")) }
        #expect(throws: ApiError.aliasUnknown) { try resolver.resolve(try alias("personal")) }
    }

    @Test("marking an alias broken keeps its binding visible to the setup UI")
    func setStatePreservesBinding() throws {
        let store = try root.open()
        try store.allowlist.upsert(try allowlistEntry("inbox"))

        #expect(try store.allowlist.setState(.broken, for: try alias("inbox")))
        let entry = try #require(try store.allowlist.entry(for: try alias("inbox")))
        #expect(entry.state == .broken)
        #expect(entry.calendarId == "cal-inbox")
        #expect(entry.titleAtBind == "List inbox")

        // An alias that is not there cannot be marked.
        #expect(try store.allowlist.setState(.broken, for: try alias("nope")) == false)
    }

    @Test("removal is reported honestly")
    func removes() throws {
        let store = try root.open()
        try store.allowlist.upsert(try allowlistEntry("inbox"))

        #expect(try store.allowlist.remove(try alias("inbox")))
        #expect(try store.allowlist.remove(try alias("inbox")) == false)
        #expect(try store.allowlist.all().isEmpty)
    }

    @Test("a hand-edited row fails closed instead of shrinking the allowlist silently")
    func rejectsCorruptRows() throws {
        let store = try root.open()
        try store.db.run(
            """
            INSERT INTO allowlist(alias, calendar_id, title_at_bind, source_at_bind, bound_at, state)
            VALUES ('NOT AN ALIAS', 'cal', 't', 's', 0, 'ok')
            """
        )
        #expect(throws: StoreError.corruptRow(table: "allowlist", column: "alias")) {
            try store.allowlist.all()
        }

        try store.db.run("DELETE FROM allowlist")
        try store.db.run(
            """
            INSERT INTO allowlist(alias, calendar_id, title_at_bind, source_at_bind, bound_at, state)
            VALUES ('inbox', 'cal', 't', 's', 0, 'maybe')
            """
        )
        #expect(throws: StoreError.corruptRow(table: "allowlist", column: "state")) {
            try store.allowlist.all()
        }
    }
}

@Suite("Reminder map repository")
struct ReminderMapRepositoryTests {
    private let root = TemporaryRoot()

    private func entry(
        bridgeId: BridgeID = BridgeID.generate(),
        itemId: String = "EK-1",
        externalId: String? = "EXT-1",
        aliasName: String = "inbox",
        createdAt: Date = Date(timeIntervalSince1970: 1_780_000_000),
        lastSeenAt: Date = Date(timeIntervalSince1970: 1_780_000_000)
    ) throws -> ReminderMapEntry {
        ReminderMapEntry(
            bridgeId: bridgeId,
            eventKitItemId: itemId,
            eventKitExternalId: externalId,
            alias: try alias(aliasName),
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

    @Test("a hand-edited bridge id fails closed")
    func rejectsCorruptRows() throws {
        let store = try root.open()
        try store.db.run(
            """
            INSERT INTO reminder_map(bridge_id, ek_item_id, ek_external_id, alias, created_at, last_seen_at)
            VALUES ('not-a-bridge-id', 'EK-1', NULL, 'inbox', 0, 0)
            """
        )
        #expect(throws: StoreError.corruptRow(table: "reminder_map", column: "bridge_id")) {
            try store.reminderMap.entry(forEventKitItemId: "EK-1")
        }
    }
}
