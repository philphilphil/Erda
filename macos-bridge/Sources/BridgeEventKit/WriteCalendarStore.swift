import BridgeCore
import Foundation

/// Where the actor reads the pinned write calendar from.
///
/// The exact shape of `ReminderIdentityStore`, and here for the same reasons: it is declared in
/// this target rather than in `BridgeCore` because nothing outside this target consumes it, and
/// `ErdaBridgeApp` — the one module that sees both `BridgeStore` and `BridgeEventKit` — supplies
/// the real implementation over `CalendarBindingRepository`.
///
/// **Read-only on purpose.** Nothing reachable from the network may re-point the write target, so
/// there is no `save` here for a handler to find. Pinning happens in the setup window and nowhere
/// else.
///
/// The method is synchronous and blocking, matching `SQLiteDB`. That is safe here and only here:
/// the actor that calls it runs on its own `DispatchSerialQueue`, never on a cooperative thread.
public protocol WriteCalendarStore: Sendable {
    /// The pinned binding, or `nil` when a human has never chosen one.
    func writeCalendar() throws -> CalendarBinding?
}

/// An in-memory `WriteCalendarStore`, for tests and for exercising the actor without a database.
/// Lives in the library rather than the test target for the same reason
/// `MemoryReminderIdentityStore` does: a test target's types are not importable from another
/// module.
public final class MemoryWriteCalendarStore: WriteCalendarStore, @unchecked Sendable {
    private let lock = NSLock()
    private var binding: CalendarBinding?
    /// When set, `writeCalendar()` throws it — for exercising the "the database is unreadable"
    /// path, which must fail closed rather than fall back to a calendar.
    private var readFailure: (any Error)?

    public init(binding: CalendarBinding? = nil) {
        self.binding = binding
    }

    public func set(_ binding: CalendarBinding?) {
        lock.lock()
        defer { lock.unlock() }
        self.binding = binding
    }

    public func setReadFailure(_ error: (any Error)?) {
        lock.lock()
        defer { lock.unlock() }
        readFailure = error
    }

    public func writeCalendar() throws -> CalendarBinding? {
        lock.lock()
        defer { lock.unlock() }
        if let readFailure { throw readFailure }
        return binding
    }
}
