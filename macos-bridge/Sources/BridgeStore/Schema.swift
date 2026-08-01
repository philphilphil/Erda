import Foundation

/// The schema from dossier §4.3, plus migration keyed on `meta.schema_version`.
public enum Schema {
    /// Bump this and append to `migrations` — never edit an existing migration, or two Macs
    /// that ran different builds end up with silently different tables at the same version.
    public static let currentVersion = 2

    static let schemaVersionKey = "schema_version"

    private static let migrations: [(version: Int, statements: [String])] = [
        (
            1,
            [
                """
                CREATE TABLE allowlist (
                    alias          TEXT PRIMARY KEY,
                    calendar_id    TEXT NOT NULL,
                    title_at_bind  TEXT NOT NULL,
                    source_at_bind TEXT NOT NULL,
                    bound_at       INTEGER NOT NULL,
                    state          TEXT NOT NULL
                )
                """,
                """
                CREATE TABLE reminder_map (
                    bridge_id      TEXT PRIMARY KEY,
                    ek_item_id     TEXT NOT NULL UNIQUE,
                    ek_external_id TEXT,
                    alias          TEXT NOT NULL,
                    created_at     INTEGER NOT NULL,
                    last_seen_at   INTEGER NOT NULL
                )
                """,
                "CREATE INDEX reminder_map_ek ON reminder_map(ek_item_id)",
                """
                CREATE TABLE idempotency (
                    key           TEXT PRIMARY KEY,
                    request_hash  BLOB NOT NULL,
                    status        INTEGER,
                    response_body BLOB,
                    created_at    INTEGER NOT NULL
                )
                """,
            ]
        ),
        (
            2,
            [
                // The allowlist is gone. Phil decided that a bridge which can reach every reminder
                // list on his own Mac is the behaviour he wants — Apple grants reminder access
                // all-or-nothing anyway — so the alias table bounded nothing and cost an
                // indirection. Lists are addressed by their real name now.
                //
                // Undoing v1's `CREATE TABLE` in v2 rather than editing v1 looks redundant on a
                // fresh database, but a v1 database already exists on that Mac: editing v1 would
                // leave it with an `alias` column this build no longer writes, and every mapping
                // insert would fail silently (they are best-effort) until nothing could be
                // completed.
                "DROP TABLE IF EXISTS allowlist",
                // Kept for diagnostics only, exactly as `alias` was: nothing resolves through it.
                // Rows written before this migration hold the old alias rather than a real list
                // name, which is harmless for the same reason.
                "ALTER TABLE reminder_map RENAME COLUMN alias TO list_name",
            ]
        ),
    ]

    /// Brings the database up to `currentVersion`, or refuses to touch it.
    ///
    /// A database written by a *newer* build is a hard stop rather than a best-effort open:
    /// this build cannot see columns it does not know about, so writing through the tables it
    /// does know would drop whatever the newer build stored there.
    @discardableResult
    public static func migrate(_ db: SQLiteDB) throws -> Int {
        try db.execute("CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL)")

        let found = try readVersion(db)
        guard found <= currentVersion else {
            throw StoreError.schemaTooNew(found: found, supported: currentVersion)
        }
        guard found < currentVersion else { return found }

        try db.transaction {
            for migration in migrations where migration.version > found {
                for statement in migration.statements {
                    try db.execute(statement)
                }
            }
            try db.run(
                "INSERT INTO meta(k, v) VALUES (?, ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v",
                [.text(schemaVersionKey), .text(String(currentVersion))]
            )
        }

        return currentVersion
    }

    /// 0 when the database has never been migrated.
    public static func readVersion(_ db: SQLiteDB) throws -> Int {
        let raw = try db.queryOne("SELECT v FROM meta WHERE k = ?", [.text(schemaVersionKey)], table: "meta") { row in
            try row.text(0, "v")
        }
        guard let raw else { return 0 }
        guard let version = Int(raw) else { throw StoreError.corruptRow(table: "meta", column: "v") }
        return version
    }
}

/// Free-form key/value settings that belong to the database rather than to a repository —
/// the bind address the setup UI last confirmed, for instance.
public struct MetaRepository: Sendable {
    let db: SQLiteDB

    public init(db: SQLiteDB) {
        self.db = db
    }

    public func value(for key: String) throws -> String? {
        try db.queryOne("SELECT v FROM meta WHERE k = ?", [.text(key)], table: "meta") { row in
            try row.text(0, "v")
        }
    }

    public func set(_ value: String, for key: String) throws {
        try db.run(
            "INSERT INTO meta(k, v) VALUES (?, ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v",
            [.text(key), .text(value)]
        )
    }

    @discardableResult
    public func remove(_ key: String) throws -> Bool {
        try db.run("DELETE FROM meta WHERE k = ?", [.text(key)]) == 1
    }
}
