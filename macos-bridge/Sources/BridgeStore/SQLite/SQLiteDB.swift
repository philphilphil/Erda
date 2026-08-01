import Foundation
import SQLite3

/// A thin wrapper over the raw `sqlite3` C API — about eight functions' worth.
///
/// Deliberately not GRDB: rejecting Hummingbird for its dependency count and then pulling in a
/// full ORM for a four-table schema would be incoherent, and `import SQLite3` is free (the SDK
/// ships the module map).
///
/// `@unchecked Sendable` with an internal recursive lock: a `sqlite3*` handle is not `Sendable`,
/// and every entry point here takes the lock, so the handle is never touched from two threads at
/// once. The lock is recursive because `transaction { }` runs its body — which calls back into
/// `run`/`query` — while already holding it.
///
/// The calls block the calling thread. That is acceptable here in a way it is not for EventKit
/// (dossier §6.3): this is a local WAL database with a single writing process, so a statement
/// costs microseconds, not an iCloud round-trip.
public final class SQLiteDB: @unchecked Sendable {
    public static let defaultBusyTimeoutMs: Int32 = 2000

    private let lock = NSRecursiveLock()
    private var handle: OpaquePointer?
    private var transactionDepth = 0

    public let path: String

    /// Opens (creating if needed) the database at `path` and applies the connection pragmas.
    ///
    /// The file is created by us rather than by SQLite so it can be born `0600`; SQLite would
    /// create it `0644 & ~umask`, leaving a window where the database is world-readable.
    public init(path: String, busyTimeoutMs: Int32 = SQLiteDB.defaultBusyTimeoutMs) throws {
        self.path = path

        if path != ":memory:", !FileManager.default.fileExists(atPath: path) {
            // An empty file is a valid empty SQLite database.
            FileManager.default.createFile(
                atPath: path,
                contents: nil,
                attributes: [.posixPermissions: NSNumber(value: FilePermissions.file)]
            )
        }

        var opened: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
        let status = sqlite3_open_v2(path, &opened, flags, nil)
        guard status == SQLITE_OK, let opened else {
            let error = SQLiteError.from(handle: opened, code: status, operation: "open")
            sqlite3_close_v2(opened)
            throw error
        }
        self.handle = opened

        sqlite3_busy_timeout(opened, busyTimeoutMs)
        // WAL survives the process being killed mid-write and lets a reader run alongside the
        // writer. `foreign_keys` is on for future constraints; the §4.3 schema declares none.
        try execute("PRAGMA journal_mode = WAL")
        try execute("PRAGMA foreign_keys = ON")
        try execute("PRAGMA synchronous = NORMAL")
    }

    deinit {
        if let handle { sqlite3_close_v2(handle) }
    }

    public func close() {
        lock.lock()
        defer { lock.unlock() }
        if let handle { sqlite3_close_v2(handle) }
        handle = nil
    }

    private func requireHandle() throws -> OpaquePointer {
        guard let handle else { throw StoreError.databaseClosed }
        return handle
    }

    // MARK: - Statements

    /// Runs one or more statements with no parameters and no results.
    public func execute(_ sql: String) throws {
        lock.lock()
        defer { lock.unlock() }
        let handle = try requireHandle()
        let status = sqlite3_exec(handle, sql, nil, nil, nil)
        guard status == SQLITE_OK else {
            throw SQLiteError.from(handle: handle, code: status, operation: "exec")
        }
    }

    /// Runs one parameterised statement that returns no rows, and reports how many rows it
    /// changed.
    ///
    /// The count is read while the lock is still held. A separate `changes` property would be
    /// racy by construction: another statement could land between the write and the read.
    @discardableResult
    public func run(_ sql: String, _ parameters: [SQLiteValue] = []) throws -> Int {
        lock.lock()
        defer { lock.unlock() }
        let handle = try requireHandle()
        let statement = try prepare(sql, parameters, handle: handle)
        defer { sqlite3_finalize(statement) }

        let status = sqlite3_step(statement)
        guard status == SQLITE_DONE || status == SQLITE_ROW else {
            throw SQLiteError.from(handle: handle, code: status, operation: "step")
        }
        return Int(sqlite3_changes(handle))
    }

    /// Runs a query and decodes every row. The row is only valid inside `decode`.
    public func query<T>(
        _ sql: String,
        _ parameters: [SQLiteValue] = [],
        table: String = "?",
        decode: (SQLiteRow) throws -> T
    ) throws -> [T] {
        lock.lock()
        defer { lock.unlock() }
        let handle = try requireHandle()
        let statement = try prepare(sql, parameters, handle: handle)
        defer { sqlite3_finalize(statement) }

        var results: [T] = []
        while true {
            let status = sqlite3_step(statement)
            if status == SQLITE_ROW {
                results.append(try decode(SQLiteRow(statement: statement, table: table)))
            } else if status == SQLITE_DONE {
                return results
            } else {
                throw SQLiteError.from(handle: handle, code: status, operation: "step")
            }
        }
    }

    /// Convenience for the single-row case.
    public func queryOne<T>(
        _ sql: String,
        _ parameters: [SQLiteValue] = [],
        table: String = "?",
        decode: (SQLiteRow) throws -> T
    ) throws -> T? {
        try query(sql, parameters, table: table, decode: decode).first
    }

    private func prepare(_ sql: String, _ parameters: [SQLiteValue], handle: OpaquePointer) throws -> OpaquePointer {
        var statement: OpaquePointer?
        let status = sqlite3_prepare_v2(handle, sql, -1, &statement, nil)
        guard status == SQLITE_OK, let statement else {
            sqlite3_finalize(statement)
            throw SQLiteError.from(handle: handle, code: status, operation: "prepare")
        }

        for (offset, value) in parameters.enumerated() {
            let index = Int32(offset + 1)
            let bindStatus: Int32
            switch value {
            case .null:
                bindStatus = sqlite3_bind_null(statement, index)
            case .integer(let number):
                bindStatus = sqlite3_bind_int64(statement, index, number)
            case .text(let string):
                bindStatus = sqlite3_bind_text(statement, index, string, -1, sqliteTransient)
            case .blob(let bytes):
                bindStatus = bytes.isEmpty
                    ? sqlite3_bind_zeroblob(statement, index, 0)
                    : bytes.withUnsafeBytes { buffer in
                        sqlite3_bind_blob(statement, index, buffer.baseAddress, Int32(buffer.count), sqliteTransient)
                    }
            }
            guard bindStatus == SQLITE_OK else {
                sqlite3_finalize(statement)
                throw SQLiteError.from(handle: handle, code: bindStatus, operation: "bind")
            }
        }

        return statement
    }

    // MARK: - Transactions

    /// Runs `body` inside a transaction, rolling back if it throws.
    ///
    /// `BEGIN IMMEDIATE` takes the write lock up front. That is what makes the idempotency
    /// claim safe: with a deferred transaction two writers can both read "no such key" before
    /// either inserts, and the loser gets `SQLITE_BUSY` at commit time instead of a clean
    /// constraint violation it can interpret.
    public func transaction<T>(immediate: Bool = true, _ body: () throws -> T) throws -> T {
        lock.lock()
        defer { lock.unlock() }

        guard transactionDepth == 0 else { throw StoreError.nestedTransaction }
        try execute(immediate ? "BEGIN IMMEDIATE" : "BEGIN")
        transactionDepth = 1

        do {
            let result = try body()
            try execute("COMMIT")
            transactionDepth = 0
            return result
        } catch {
            // A failed rollback would mean the handle is unusable; the original error is the
            // more useful one to report, so this deliberately swallows the secondary failure.
            try? execute("ROLLBACK")
            transactionDepth = 0
            throw error
        }
    }
}
