import Foundation
import SQLite3

/// Extended result codes. The C header defines these as compound macros
/// (`SQLITE_CONSTRAINT | (8<<8)`), which the Swift importer refuses to bring across
/// ("structure not supported"), so they are restated here.
enum SQLiteExtendedCode {
    static let constraintPrimaryKey = SQLITE_CONSTRAINT | (6 << 8)  // 1555
    static let constraintUnique = SQLITE_CONSTRAINT | (8 << 8)  // 2067
}

/// A failure reported by SQLite itself.
///
/// `message` comes from `sqlite3_errmsg` and names tables and columns — never row values —
/// so it is safe for the local log. It must still never reach a response body: handlers map
/// any store failure to `ApiError.internal`, which carries no message field at all.
public struct SQLiteError: Error, Equatable, CustomStringConvertible {
    public let code: Int32
    public let extendedCode: Int32
    public let message: String
    /// The operation that failed, for the local log. A fixed string, never interpolated
    /// with user input.
    public let operation: String

    public var isConstraintViolation: Bool { code == SQLITE_CONSTRAINT }
    public var isBusy: Bool { code == SQLITE_BUSY || code == SQLITE_LOCKED }

    public var description: String {
        "sqlite \(operation) failed: code \(code) (extended \(extendedCode)): \(message)"
    }

    static func from(handle: OpaquePointer?, code: Int32, operation: String) -> SQLiteError {
        SQLiteError(
            code: code & 0xFF,
            extendedCode: handle.map { sqlite3_extended_errcode($0) } ?? code,
            message: handle.map { String(cString: sqlite3_errmsg($0)) } ?? "no handle",
            operation: operation
        )
    }
}

/// Failures above the SQLite layer.
public enum StoreError: Error, Equatable, CustomStringConvertible {
    /// The database was written by a newer build. Refusing to run is the only safe choice:
    /// opening it read-write would silently ignore columns and tables this build cannot see,
    /// and writing through them loses data.
    case schemaTooNew(found: Int, supported: Int)
    /// A stored value no longer satisfies its own type's rules (a hand-edited list name, say).
    /// Fails closed rather than skipping the row, which would quietly lose a mapping.
    case corruptRow(table: String, column: String)
    case nestedTransaction
    case databaseClosed
    /// `complete()` was called for a key that is not in flight.
    case idempotencyRowNotInFlight

    public var description: String {
        switch self {
        case .schemaTooNew(let found, let supported):
            "database schema version \(found) is newer than this build supports (\(supported))"
        case .corruptRow(let table, let column):
            "unreadable value in \(table).\(column)"
        case .nestedTransaction:
            "nested transactions are not supported"
        case .databaseClosed:
            "database handle is closed"
        case .idempotencyRowNotInFlight:
            "idempotency row is not in flight"
        }
    }
}
