import EventKit
import Foundation

/// A one-shot rendezvous between `fetchReminders(matching:completion:)` and the caller awaiting
/// it.
///
/// Two things can finish the fetch and they genuinely race: EventKit's completion block, invoked
/// on a queue we do not control, and our own timeout. Resuming a `CheckedContinuation` twice is a
/// **crash**, not an error, so the single resume is enforced under a lock rather than by argument.
///
/// The gate is also what lets the fetch handle stay a local of the actor-isolated frame that
/// started it: nothing here captures the `EKEventStore` or the opaque handle, so no `@Sendable`
/// closure ever has to hold a non-`Sendable` EventKit value.
///
/// `@unchecked Sendable` because it is a lock-protected mutable box — the same shape as
/// `BridgeCore.MemoryIdempotencyStore`.
final class FetchGate: @unchecked Sendable {
    private let lock = NSLock()
    private var finished = false
    /// A result that arrived before anyone awaited it.
    private var pending: Result<[RawReminder], any Error>?
    private var waiter: CheckedContinuation<[RawReminder], any Error>?

    /// Delivers the first result and ignores every later one. Safe from any thread.
    func finish(_ result: Result<[RawReminder], any Error>) {
        lock.lock()
        guard !finished else {
            lock.unlock()
            return
        }
        finished = true
        let waiter = self.waiter
        self.waiter = nil
        if waiter == nil { pending = result }
        lock.unlock()

        // Resumed outside the lock: a continuation can run its caller synchronously.
        waiter?.resume(with: result)
    }

    /// Awaited exactly once, by the frame that started the fetch.
    var value: [RawReminder] {
        get async throws {
            try await withCheckedThrowingContinuation { continuation in
                lock.lock()
                if let ready = pending {
                    pending = nil
                    lock.unlock()
                    continuation.resume(with: ready)
                    return
                }
                waiter = continuation
                lock.unlock()
            }
        }
    }
}

/// Watches `EKEventStoreChangedNotification` and does nothing but raise a flag.
///
/// The notification arrives on an arbitrary thread and means *every* `EKCalendar` and `EKReminder`
/// previously fetched is now invalid (`EKEventStore.h`). The correct response is
/// `EKEventStore.reset()` — but calling it from the notification would race a fetch that is
/// already in flight on the actor's queue. Raising a flag instead lets the reset happen on that
/// queue, between operations, where it cannot race anything.
///
/// The header also notes the notification fires "if access to events or reminders is changed by
/// the user", which is why a revocation reaches the actor at all.
final class EventStoreChangeFlag: @unchecked Sendable {
    /// Named once here so tests can post the real notification instead of guessing its string.
    static let notificationName = Notification.Name.EKEventStoreChanged

    private let lock = NSLock()
    private var pending = false
    private let center: NotificationCenter
    private var token: (any NSObjectProtocol)?

    /// - Parameter observing: pass `false` to build a flag with no subscription, for tests that
    ///   drive `raise()` directly.
    init(center: NotificationCenter = .default, observing: Bool = true) {
        self.center = center
        guard observing else { return }
        // Captures only `self`, which is `Sendable`; the `Notification` argument is discarded, so
        // nothing non-`Sendable` is ever read out of it.
        token = center.addObserver(
            forName: Self.notificationName,
            object: nil,
            queue: nil
        ) { [weak self] _ in
            self?.raise()
        }
    }

    deinit {
        if let token { center.removeObserver(token) }
    }

    func raise() {
        lock.lock()
        defer { lock.unlock() }
        pending = true
    }

    /// Reads and clears the flag. `true` means a reset is owed before the next EventKit call.
    func consume() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let wasPending = pending
        pending = false
        return wasPending
    }
}
