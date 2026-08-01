import BridgeCore
import Foundation

/// Persistence for the alias → Reminders list bindings.
///
/// There is deliberately no lookup by title anywhere in this type. Apple's own advice for a
/// lost `calendarIdentifier` is "fall back to title" (`EKCalendar.h`); doing that automatically
/// is how a resync would start writing inbox items into a stranger's shared list. Re-binding is
/// a human decision made in the local setup UI, and the remote API has no route to it.
public struct AllowlistRepository: Sendable {
    private let db: SQLiteDB

    public init(db: SQLiteDB) {
        self.db = db
    }

    public func all() throws -> [AllowlistEntry] {
        try db.query(
            """
            SELECT alias, calendar_id, title_at_bind, source_at_bind, bound_at, state
            FROM allowlist ORDER BY alias
            """,
            table: "allowlist",
            decode: Self.decode
        )
    }

    /// The whole table as the resolver `BridgeCore` exposes.
    public func load() throws -> Allowlist {
        Allowlist(entries: try all())
    }

    public func entry(for alias: Alias) throws -> AllowlistEntry? {
        try db.queryOne(
            """
            SELECT alias, calendar_id, title_at_bind, source_at_bind, bound_at, state
            FROM allowlist WHERE alias = ?
            """,
            [.text(alias.rawValue)],
            table: "allowlist",
            decode: Self.decode
        )
    }

    /// Binds an alias, or re-binds an existing one. Called only from the setup UI.
    public func upsert(_ entry: AllowlistEntry) throws {
        try db.run(
            """
            INSERT INTO allowlist(alias, calendar_id, title_at_bind, source_at_bind, bound_at, state)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(alias) DO UPDATE SET
                calendar_id    = excluded.calendar_id,
                title_at_bind  = excluded.title_at_bind,
                source_at_bind = excluded.source_at_bind,
                bound_at       = excluded.bound_at,
                state          = excluded.state
            """,
            [
                .text(entry.alias.rawValue),
                .text(entry.calendarId),
                .text(entry.titleAtBind),
                .text(entry.sourceAtBind),
                .date(entry.boundAt),
                .text(entry.state.rawValue),
            ]
        )
    }

    /// Marks an alias broken (or healthy again) without disturbing its binding, so the setup UI
    /// can still show which list it used to point at.
    @discardableResult
    public func setState(_ state: AllowlistState, for alias: Alias) throws -> Bool {
        try db.run(
            "UPDATE allowlist SET state = ? WHERE alias = ?",
            [.text(state.rawValue), .text(alias.rawValue)]
        ) == 1
    }

    @discardableResult
    public func remove(_ alias: Alias) throws -> Bool {
        try db.run("DELETE FROM allowlist WHERE alias = ?", [.text(alias.rawValue)]) == 1
    }

    private static func decode(_ row: SQLiteRow) throws -> AllowlistEntry {
        guard let alias = Alias(rawValue: try row.text(0, "alias")) else {
            throw StoreError.corruptRow(table: "allowlist", column: "alias")
        }
        guard let state = AllowlistState(rawValue: try row.text(5, "state")) else {
            throw StoreError.corruptRow(table: "allowlist", column: "state")
        }
        return AllowlistEntry(
            alias: alias,
            calendarId: try row.text(1, "calendar_id"),
            titleAtBind: try row.text(2, "title_at_bind"),
            sourceAtBind: try row.text(3, "source_at_bind"),
            boundAt: try row.date(4, "bound_at"),
            state: state
        )
    }
}
