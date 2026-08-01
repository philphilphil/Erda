import Foundation

/// Where audit lines go. Deliberately non-throwing and non-async: auditing runs on every request
/// including rejected ones, and a failing log must never be able to fail a request or add a
/// suspension point to the hot path. The real sink (`BridgeStore`, M2) swallows and self-reports
/// its own I/O errors.
public protocol AuditSink: Sendable {
    func record(_ event: AuditEvent)
}

/// Keeps events in memory. Lives in the library rather than a test target so `BridgeHTTP`'s
/// integration tests can assert on what the handlers audited.
public final class MemoryAuditSink: AuditSink, @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [AuditEvent] = []

    public init() {}

    public func record(_ event: AuditEvent) {
        lock.lock()
        defer { lock.unlock() }
        storage.append(event)
    }

    public var events: [AuditEvent] {
        lock.lock()
        defer { lock.unlock() }
        return storage
    }

    /// The serialised lines, as they would appear in `audit.jsonl`.
    public func lines() throws -> [String] {
        try events.map { try $0.jsonLine() }
    }

    public func reset() {
        lock.lock()
        defer { lock.unlock() }
        storage.removeAll()
    }
}
