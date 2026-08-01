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
        #expect(tables.contains("allowlist"))
        #expect(tables.contains("reminder_map"))
        #expect(tables.contains("idempotency"))

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
            try store.allowlist.upsert(try allowlistEntry("inbox"))
            store.close()
        }

        let reopened = try root.open()
        #expect(reopened.schemaVersion == Schema.currentVersion)
        #expect(try reopened.allowlist.all().count == 1)
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
            try store.allowlist.upsert(try allowlistEntry("inbox"))
            try store.meta.set("99", for: Schema.schemaVersionKey)
            store.close()
        }

        #expect(throws: (any Error).self) { try root.open() }

        // Reading it back with the version restored must show the row untouched.
        let db = try root.openRawConnection()
        try db.run("UPDATE meta SET v = ? WHERE k = ?", [.text("1"), .text(Schema.schemaVersionKey)])
        #expect(try AllowlistRepository(db: db).all().count == 1)
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
                try store.allowlist.upsert(try allowlistEntry("inbox"))
                throw Boom()
            }
        }
        #expect(try store.allowlist.all().isEmpty)

        // The handle is still usable afterwards.
        try store.allowlist.upsert(try allowlistEntry("work"))
        #expect(try store.allowlist.all().count == 1)
    }

    @Test("a closed handle reports itself rather than crashing")
    func reportsClosedHandle() throws {
        let store = try root.open()
        store.close()
        #expect(throws: StoreError.databaseClosed) {
            try store.allowlist.all()
        }
    }
}
