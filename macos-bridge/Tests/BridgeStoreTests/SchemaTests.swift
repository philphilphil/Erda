import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

@Suite("Schema and migration")
struct SchemaTests {
    private let root = TemporaryRoot()

    @Test("a fresh database migrates to the current version and gets every table")
    func migratesFreshDatabase() throws {
        let store = try root.open()
        #expect(store.schemaVersion == Schema.currentVersion)

        let tables = try store.db.query(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name",
            table: "sqlite_master"
        ) { try $0.text(0, "name") }
        #expect(tables.contains("meta"))
        #expect(tables.contains("reminder_map"))
        #expect(tables.contains("idempotency"))
        // v2 dropped it. A fresh database creates it in v1 and drops it again a moment later,
        // which looks silly but keeps the "never edit a shipped migration" rule intact for the
        // database that already exists on Phil's Mac.
        #expect(!tables.contains("allowlist"))

        let indexes = try store.db.query(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%'",
            table: "sqlite_master"
        ) { try $0.text(0, "name") }
        #expect(indexes.contains("reminder_map_ek"))
    }

    @Test("the connection pragmas are applied")
    func appliesPragmas() throws {
        let store = try root.open()

        let journalMode = try store.db.queryOne("PRAGMA journal_mode", table: "pragma") {
            try $0.text(0, "journal_mode")
        }
        #expect(journalMode == "wal")

        let foreignKeys = try store.db.queryOne("PRAGMA foreign_keys", table: "pragma") {
            try $0.integer(0, "foreign_keys")
        }
        #expect(foreignKeys == 1)
    }

    @Test("re-opening an existing database is a no-op that preserves data")
    func reopenIsIdempotent() throws {
        do {
            let store = try root.open()
            try store.reminderMap.insert(try mapEntry())
            store.close()
        }

        let reopened = try root.open()
        #expect(reopened.schemaVersion == Schema.currentVersion)
        #expect(try reopened.reminderMap.count() == 1)
    }

    /// The migration a database written by the allowlist-era build has to survive: the table goes,
    /// the id map stays, and the ids it holds keep resolving. Losing those rows would 404 every
    /// reminder the bridge had already created.
    @Test("a v1 database migrates by dropping the allowlist and keeping the id map")
    func migratesFromVersionOne() throws {
        let bridgeId = BridgeID.generate()
        do {
            // v1 exactly as it shipped, written by hand: no build in the migration list produces
            // this shape any more.
            try root.directories.create()
            let db = try SQLiteDB(path: root.directories.databaseURL.path)
            try db.execute("CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL)")
            try db.execute("""
                CREATE TABLE allowlist (
                    alias          TEXT PRIMARY KEY,
                    calendar_id    TEXT NOT NULL,
                    title_at_bind  TEXT NOT NULL,
                    source_at_bind TEXT NOT NULL,
                    bound_at       INTEGER NOT NULL,
                    state          TEXT NOT NULL
                )
                """)
            try db.execute("""
                CREATE TABLE reminder_map (
                    bridge_id      TEXT PRIMARY KEY,
                    ek_item_id     TEXT NOT NULL UNIQUE,
                    ek_external_id TEXT,
                    alias          TEXT NOT NULL,
                    created_at     INTEGER NOT NULL,
                    last_seen_at   INTEGER NOT NULL
                )
                """)
            try db.execute("""
                CREATE TABLE idempotency (
                    key           TEXT PRIMARY KEY,
                    request_hash  BLOB NOT NULL,
                    status        INTEGER,
                    response_body BLOB,
                    created_at    INTEGER NOT NULL
                )
                """)
            try db.run(
                "INSERT INTO allowlist VALUES ('inbox', 'cal-1', 'Inbox', 'iCloud', 0, 'ok')"
            )
            try db.run(
                """
                INSERT INTO reminder_map(bridge_id, ek_item_id, ek_external_id, alias, created_at, last_seen_at)
                VALUES (?, 'ek-legacy', NULL, 'inbox', 0, 0)
                """,
                [.text(bridgeId.rawValue)]
            )
            try db.run(
                "INSERT INTO meta(k, v) VALUES (?, '1')",
                [.text(Schema.schemaVersionKey)]
            )
            db.close()
        }

        let store = try root.open()
        #expect(store.schemaVersion == Schema.currentVersion)

        let tables = try store.db.query(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name",
            table: "sqlite_master"
        ) { try $0.text(0, "name") }
        #expect(!tables.contains("allowlist"))
        #expect(tables.contains("reminder_map"))

        // The row survives the column rename, and the old alias sitting in `list_name` is
        // harmless: nothing resolves through it.
        let entry = try #require(try store.reminderMap.entry(for: bridgeId))
        #expect(entry.eventKitItemId == "ek-legacy")
        #expect(entry.listName.rawValue == "inbox")
    }

    @Test("a database written by a newer build is refused, not opened read-write")
    func refusesNewerSchema() throws {
        do {
            let store = try root.open()
            try store.meta.set("99", for: Schema.schemaVersionKey)
            store.close()
        }

        #expect(throws: StoreError.schemaTooNew(found: 99, supported: Schema.currentVersion)) {
            try root.open()
        }
    }

    @Test("the refusal happens before anything is written")
    func refusalDoesNotMutate() throws {
        do {
            let store = try root.open()
            try store.reminderMap.insert(try mapEntry())
            try store.meta.set("99", for: Schema.schemaVersionKey)
            store.close()
        }

        #expect(throws: (any Error).self) { try root.open() }

        // Reading it back with the version restored must show the row untouched.
        let db = try root.openRawConnection()
        try db.run(
            "UPDATE meta SET v = ? WHERE k = ?",
            [.text(String(Schema.currentVersion)), .text(Schema.schemaVersionKey)]
        )
        #expect(try ReminderMapRepository(db: db).count() == 1)
    }

    @Test("an unreadable version string is a corrupt row, not a silent version 0")
    func rejectsNonNumericVersion() throws {
        do {
            let store = try root.open()
            try store.meta.set("banana", for: Schema.schemaVersionKey)
            store.close()
        }

        #expect(throws: StoreError.corruptRow(table: "meta", column: "v")) {
            try root.open()
        }
    }

    @Test("meta stores arbitrary settings")
    func metaRoundTrips() throws {
        let store = try root.open()
        #expect(try store.meta.value(for: "bind_ip") == nil)

        try store.meta.set("192.168.178.106", for: "bind_ip")
        #expect(try store.meta.value(for: "bind_ip") == "192.168.178.106")

        try store.meta.set("192.168.178.107", for: "bind_ip")
        #expect(try store.meta.value(for: "bind_ip") == "192.168.178.107")
    }

    @Test("nested transactions are refused rather than silently flattened")
    func refusesNestedTransactions() throws {
        let store = try root.open()
        #expect(throws: StoreError.nestedTransaction) {
            try store.db.transaction {
                try store.db.transaction { }
            }
        }
    }

    @Test("a throwing transaction body rolls back")
    func rollsBackOnFailure() throws {
        struct Boom: Error {}
        let store = try root.open()

        #expect(throws: Boom.self) {
            try store.db.transaction {
                try store.reminderMap.insert(try mapEntry())
                throw Boom()
            }
        }
        #expect(try store.reminderMap.count() == 0)

        // The handle is still usable afterwards.
        try store.reminderMap.insert(try mapEntry("Work"))
        #expect(try store.reminderMap.count() == 1)
    }

    @Test("a closed handle reports itself rather than crashing")
    func reportsClosedHandle() throws {
        let store = try root.open()
        store.close()
        #expect(throws: StoreError.databaseClosed) {
            _ = try store.reminderMap.count()
        }
    }
}
