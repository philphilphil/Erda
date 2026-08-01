import Foundation
import SQLite3

/// `SQLITE_TRANSIENT` — tells SQLite to copy the bound bytes, so the Swift buffer does not
/// have to outlive the bind call. The C macro casts -1 to a destructor pointer and the Swift
/// importer does not bring it across.
let sqliteTransient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

/// A bindable parameter.
public enum SQLiteValue: Equatable, Sendable {
    case null
    case integer(Int64)
    case text(String)
    case blob([UInt8])

    public static func int(_ value: Int) -> SQLiteValue { .integer(Int64(value)) }

    /// Timestamps are stored as whole epoch seconds, matching the `INTEGER` columns in §4.3.
    public static func date(_ value: Date) -> SQLiteValue { .integer(Int64(value.timeIntervalSince1970.rounded(.down))) }

    public static func optionalText(_ value: String?) -> SQLiteValue { value.map { .text($0) } ?? .null }
}

/// Column access for one result row. Never escapes the `query` closure that produced it —
/// the underlying statement is finalised as soon as the query returns.
public struct SQLiteRow {
    let statement: OpaquePointer
    let table: String

    private func isNull(_ index: Int32) -> Bool {
        sqlite3_column_type(statement, index) == SQLITE_NULL
    }

    public func text(_ index: Int32, _ column: String) throws -> String {
        guard let value = optionalText(index) else { throw StoreError.corruptRow(table: table, column: column) }
        return value
    }

    public func optionalText(_ index: Int32) -> String? {
        guard !isNull(index), let pointer = sqlite3_column_text(statement, index) else { return nil }
        return String(cString: pointer)
    }

    public func integer(_ index: Int32, _ column: String) throws -> Int64 {
        guard let value = optionalInteger(index) else { throw StoreError.corruptRow(table: table, column: column) }
        return value
    }

    public func optionalInteger(_ index: Int32) -> Int64? {
        isNull(index) ? nil : sqlite3_column_int64(statement, index)
    }

    public func date(_ index: Int32, _ column: String) throws -> Date {
        Date(timeIntervalSince1970: TimeInterval(try integer(index, column)))
    }

    public func blob(_ index: Int32, _ column: String) throws -> [UInt8] {
        guard let value = optionalBlob(index) else { throw StoreError.corruptRow(table: table, column: column) }
        return value
    }

    public func optionalBlob(_ index: Int32) -> [UInt8]? {
        guard !isNull(index) else { return nil }
        let count = Int(sqlite3_column_bytes(statement, index))
        // A zero-length blob is a legitimate value and `sqlite3_column_blob` returns NULL for
        // it, which must not be confused with SQL NULL — hence the type check above.
        guard count > 0, let pointer = sqlite3_column_blob(statement, index) else { return [] }
        return [UInt8](UnsafeRawBufferPointer(start: pointer, count: count))
    }
}
