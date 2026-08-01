import BridgeCore
import Foundation

/// The persistence seam this target needs, kept as a protocol so `BridgeEventKit` links no
/// SQLite and can be exercised entirely in memory.
///
/// It is deliberately *not* declared in `BridgeCore`: unlike `RemindersService` or
/// `IdempotencyStore`, nothing outside this target consumes it. `ErdaBridgeApp` — the one module
/// that already depends on both `BridgeStore` and `BridgeEventKit` — supplies the real
/// implementation over `ReminderMapRepository` and `AllowlistRepository`.
///
/// Every method is synchronous and blocking, matching `SQLiteDB`. That is safe here and only
/// here: the actor that calls them runs on its own `DispatchSerialQueue`, never on a cooperative
/// thread.
public protocol ReminderIdentityStore: Sendable {
    /// Persists a `rem_<uuid>` ↔ `calendarItemIdentifier` binding.
    ///
    /// Written after a successful save, and again the first time `list` sees a reminder the
    /// bridge did not create — without a mapping a reminder can never be completed.
    func recordMapping(
        bridgeId: BridgeID,
        itemId: String,
        externalId: String?,
        alias: Alias,
        at date: Date
    ) throws

    func itemId(for bridgeId: BridgeID) throws -> String?

    func bridgeId(forItemId itemId: String) throws -> BridgeID?

    /// Resets the pruning clock for a mapping EventKit still resolves.
    func touch(_ bridgeId: BridgeID, at date: Date) throws

    /// Records that an alias' bound `calendarIdentifier` no longer resolves.
    ///
    /// This only ever *marks*. Re-binding by title — which Apple suggests in `EKCalendar.h` — is
    /// how a bridge would start writing into a stranger's shared list after an iCloud resync, so
    /// it is a human decision made in the local setup UI and has no code path here.
    func markAliasBroken(_ alias: Alias) throws
}

/// An in-memory `ReminderIdentityStore`, for tests and for exercising the actor without a
/// database. Lives in the library rather than the test target for the same reason as
/// `BridgeCore.FakeReminders`: a test target's types are not importable from another module.
public final class MemoryReminderIdentityStore: ReminderIdentityStore, @unchecked Sendable {
    private let lock = NSLock()
    private var itemIdsByBridgeId: [BridgeID: String] = [:]
    private var bridgeIdsByItemId: [String: BridgeID] = [:]
    private var lastSeen: [BridgeID: Date] = [:]
    private var broken: Set<Alias> = []
    /// When set, every mutating call throws it — for exercising the best-effort write paths.
    private var writeFailure: (any Error)?

    public init() {}

    // MARK: - Test controls

    public func setWriteFailure(_ error: (any Error)?) {
        lock.lock()
        defer { lock.unlock() }
        writeFailure = error
    }

    public var brokenAliases: Set<Alias> {
        lock.lock()
        defer { lock.unlock() }
        return broken
    }

    public var mappingCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return itemIdsByBridgeId.count
    }

    public func lastSeen(for bridgeId: BridgeID) -> Date? {
        lock.lock()
        defer { lock.unlock() }
        return lastSeen[bridgeId]
    }

    // MARK: - ReminderIdentityStore

    public func recordMapping(
        bridgeId: BridgeID,
        itemId: String,
        externalId: String?,
        alias: Alias,
        at date: Date
    ) throws {
        lock.lock()
        defer { lock.unlock() }
        if let writeFailure { throw writeFailure }
        itemIdsByBridgeId[bridgeId] = itemId
        bridgeIdsByItemId[itemId] = bridgeId
        lastSeen[bridgeId] = date
    }

    public func itemId(for bridgeId: BridgeID) throws -> String? {
        lock.lock()
        defer { lock.unlock() }
        return itemIdsByBridgeId[bridgeId]
    }

    public func bridgeId(forItemId itemId: String) throws -> BridgeID? {
        lock.lock()
        defer { lock.unlock() }
        return bridgeIdsByItemId[itemId]
    }

    public func touch(_ bridgeId: BridgeID, at date: Date) throws {
        lock.lock()
        defer { lock.unlock() }
        if let writeFailure { throw writeFailure }
        guard itemIdsByBridgeId[bridgeId] != nil else { return }
        lastSeen[bridgeId] = date
    }

    public func markAliasBroken(_ alias: Alias) throws {
        lock.lock()
        defer { lock.unlock() }
        if let writeFailure { throw writeFailure }
        broken.insert(alias)
    }
}
