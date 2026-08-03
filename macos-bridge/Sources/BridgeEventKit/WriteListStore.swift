import BridgeCore
import Foundation

/// Where the actor reads the pinned write list from.
///
/// The reminder counterpart of `WriteCalendarStore`, declared here for the same reasons: it is in
/// this target rather than in `BridgeCore` because nothing outside this target consumes it, and
/// `ErdaBridgeApp` — the one module that sees both `BridgeStore` and `BridgeEventKit` — supplies the
/// real implementation over `ListBindingRepository`.
///
/// **Read-only on purpose.** Nothing reachable from the network may re-point the write target, so
/// there is no `save` here for a handler to find. Pinning happens in the setup window and nowhere
/// else.
///
/// The method is synchronous and blocking, matching `SQLiteDB`. That is safe here and only here:
/// the actor that calls it runs on its own `DispatchSerialQueue`, never on a cooperative thread.
public protocol WriteListStore: Sendable {
    /// The pinned binding, or `nil` when a human has never chosen one.
    func writeList() throws -> ListBinding?
}

/// An in-memory `WriteListStore`, for tests and for exercising the actor without a database. Lives
/// in the library rather than the test target for the same reason `MemoryWriteCalendarStore` does:
/// a test target's types are not importable from another module.
public final class MemoryWriteListStore: WriteListStore, @unchecked Sendable {
    private let lock = NSLock()
    private var binding: ListBinding?
    /// When set, `writeList()` throws it — for exercising the "the database is unreadable" path,
    /// which must fail closed rather than fall back to a list.
    private var readFailure: (any Error)?

    public init(binding: ListBinding? = nil) {
        self.binding = binding
    }

    public func set(_ binding: ListBinding?) {
        lock.lock()
        defer { lock.unlock() }
        self.binding = binding
    }

    public func setReadFailure(_ error: (any Error)?) {
        lock.lock()
        defer { lock.unlock() }
        readFailure = error
    }

    public func writeList() throws -> ListBinding? {
        lock.lock()
        defer { lock.unlock() }
        if let readFailure { throw readFailure }
        return binding
    }
}
