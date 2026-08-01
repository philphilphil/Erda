import Foundation

/// The time seam.
///
/// Named `BridgeClock` rather than `Clock` on purpose: the standard library already has a
/// `Swift.Clock` protocol, and a same-named protocol in this module would force every call site
/// to disambiguate. This one is deliberately much smaller — wall-clock instants only, because
/// that is what rate-limit refills, idempotency TTLs and audit timestamps are expressed in.
public protocol BridgeClock: Sendable {
    var now: Date { get }
}

public struct SystemClock: BridgeClock {
    public init() {}
    public var now: Date { Date() }
}

/// A clock that only moves when a test moves it. Lives in the library rather than a test target
/// so `BridgeStore` and `BridgeHTTP` can drive their own time-dependent tests with it.
public final class ManualClock: BridgeClock, @unchecked Sendable {
    private let lock = NSLock()
    private var current: Date

    public init(now: Date = Date(timeIntervalSince1970: 1_780_000_000)) {
        self.current = now
    }

    public var now: Date {
        lock.lock()
        defer { lock.unlock() }
        return current
    }

    public func advance(by interval: TimeInterval) {
        lock.lock()
        defer { lock.unlock() }
        current = current.addingTimeInterval(interval)
    }

    public func set(_ date: Date) {
        lock.lock()
        defer { lock.unlock() }
        current = date
    }
}
