import Foundation

/// Passes every event through to another sink and remembers the most recent one.
///
/// This is how the setup UI answers "when did Erda last talk to this bridge?" without reading the
/// JSONL file back or growing a second, unaudited notification path. It cannot leak anything the
/// log does not already hold: an `AuditEvent` has no free-form `String` field at all.
///
/// Wrapping rather than replacing matters — the durable write happens first, so a crash between
/// the two lines loses the UI's readout and not the audit record.
public final class LastEventAuditSink: AuditSink, @unchecked Sendable {
    private let lock = NSLock()
    private let wrapped: any AuditSink
    private var latest: AuditEvent?
    private var count = 0

    public init(wrapping wrapped: any AuditSink) {
        self.wrapped = wrapped
    }

    public func record(_ event: AuditEvent) {
        wrapped.record(event)
        lock.lock()
        defer { lock.unlock() }
        latest = event
        count += 1
    }

    /// `nil` until the first request of this process — including a rejected one, since auditing
    /// runs on those too.
    public var lastEvent: AuditEvent? {
        lock.lock()
        defer { lock.unlock() }
        return latest
    }

    /// Requests seen since launch, for the status readout.
    public var eventCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return count
    }
}
