import BridgeCore
import Dispatch
import EventKit
import Foundation

/// The real `RemindersService`: EventKit, confined to one actor on one serial queue.
///
/// ## Why a custom executor
///
/// `saveReminder:commit:error:` is **synchronous and blocking** — on an iCloud list it is a
/// network round trip. A plain actor runs its body on Swift's cooperative pool, whose thread count
/// is the core count, so one save would park a thread that the whole process needs for unrelated
/// work; a handful of concurrent requests would starve the NIO channels feeding them. Backing the
/// actor with a dedicated `DispatchSerialQueue` moves that blocking off the pool entirely, and the
/// serialisation comes for free: EventKit mutations through one `EKEventStore` are not safe to
/// interleave.
///
/// ## Why nothing here is `nonisolated`
///
/// `EKEventStore`, `EKCalendar` and `EKReminder` carry no `Sendable` annotation anywhere in the
/// EventKit headers. Keeping the store as actor-isolated state is what makes "no EventKit type
/// crosses an isolation boundary" a compiler-checked fact: the only value that leaves is a
/// `RawReminder`, built inside the fetch completion (see `Mapping.swift`).
public actor EventKitReminders: RemindersService {
    // MARK: - Custom serial executor

    private let queue: DispatchSerialQueue

    public nonisolated var unownedExecutor: UnownedSerialExecutor {
        queue.asUnownedSerialExecutor()
    }

    // MARK: - State

    /// Long-lived by design: the header asks for one store per process, and objects fetched from
    /// one store cannot be used with another.
    private let store: EKEventStore
    private let allowlistProvider: @Sendable () async -> Allowlist
    private let identity: any ReminderIdentityStore
    private let clock: any BridgeClock
    /// The zone a due date is *expressed in* once it reaches Reminders.app. The wire format
    /// requires an offset-bearing timestamp, so the instant is already unambiguous; this only
    /// decides which wall-clock time the user sees.
    private let timeZone: TimeZone
    private let fetchTimeout: Duration
    private let changes: EventStoreChangeFlag

    public init(
        allowlist: @escaping @Sendable () async -> Allowlist,
        identity: any ReminderIdentityStore,
        clock: any BridgeClock = SystemClock(),
        timeZone: TimeZone = .current,
        fetchTimeout: Duration = .seconds(10),
        observingChanges: Bool = true
    ) {
        self.queue = DispatchSerialQueue(
            label: "de.philippbaum.erdabridge.eventkit",
            qos: .userInitiated
        )
        self.store = EKEventStore()
        self.allowlistProvider = allowlist
        self.identity = identity
        self.clock = clock
        self.timeZone = timeZone
        self.fetchTimeout = fetchTimeout
        self.changes = EventStoreChangeFlag(observing: observingChanges)
    }

    // MARK: - RemindersService

    /// Deliberately does not touch the store: it is called on every request, and
    /// `authorizationStatus(for:)` is a class method that is always current — including after a
    /// revocation that has not yet produced a change notification.
    public func availability() async -> ReminderAvailability {
        let allowlist = await allowlistProvider()
        return allowlist.availability(authorized: RemindersAccess.status().isUsable)
    }

    public func list(_ query: ListRemindersQuery) async throws -> [ReminderSnapshot] {
        let allowlist = await allowlistProvider()
        try prepare()

        let requested = query.aliases.isEmpty ? allowlist.healthyAliases : query.aliases
        // An empty alias set would make `predicateForReminders(in:)` mean "every list on this
        // Mac" if it were passed through as an empty array — the exact leak the allowlist exists
        // to prevent. Nothing to read from is `reminders_unavailable`, never a broad fetch.
        guard !requested.isEmpty else { throw ApiError.remindersUnavailable }

        var calendars: [EKCalendar] = []
        var aliasByCalendarId: [String: Alias] = [:]
        // A repeated alias in the query must not become a repeated calendar in the predicate.
        for alias in Set(requested).sorted() {
            let entry = try allowlist.resolve(alias)
            let calendar = try resolveCalendar(entry)
            guard aliasByCalendarId[calendar.calendarIdentifier] == nil else { continue }
            calendars.append(calendar)
            aliasByCalendarId[calendar.calendarIdentifier] = alias
        }

        // NOT `predicateForIncompleteReminders(withDueDateStarting:ending:calendars:)`. Its
        // nil/nil window is documented as "all incomplete reminders" but the header does not say
        // whether a reminder with no due date falls inside a date window at all, and silently
        // dropping every undated reminder is exactly the kind of bug nobody notices for months.
        // Fetching everything in the allowed calendars and filtering `!isCompleted` in Swift is
        // slower and provably right.
        let raw = try await fetch(matching: store.predicateForReminders(in: calendars))

        let now = clock.now
        var snapshots: [ReminderSnapshot] = []
        for reminder in raw where !reminder.isCompleted {
            // Defence in depth: the predicate already restricted the fetch to these calendars,
            // so anything else here would mean EventKit ignored it.
            guard let calendarId = reminder.calendarId, let alias = aliasByCalendarId[calendarId] else {
                continue
            }
            snapshots.append(reminder.snapshot(id: bridgeId(for: reminder, alias: alias, now: now), alias: alias))
        }

        // Sorted by what a caller reads it for — soonest due first, undated last — with the id as
        // a final tiebreak so the order is stable across calls.
        snapshots.sort { left, right in
            let leftDue = left.dueAt ?? .distantFuture
            let rightDue = right.dueAt ?? .distantFuture
            if leftDue != rightDue { return leftDue < rightDue }
            if left.title != right.title { return left.title < right.title }
            return left.id.rawValue < right.id.rawValue
        }
        return Array(snapshots.prefix(query.limit))
    }

    public func create(_ command: CreateReminderCommand) async throws -> ReminderSnapshot {
        let allowlist = await allowlistProvider()
        try prepare()

        let entry = try allowlist.resolve(command.alias)
        let calendar = try resolveCalendar(entry)
        // A calendar that resolves but cannot hold a reminder is a binding a human has to fix.
        // Checking here turns an `EKErrorCalendarReadOnly` round trip into an immediate 409, and
        // the alias is *not* marked broken — the list still exists, it is just the wrong one.
        guard calendar.allowsContentModifications, calendar.allowedEntityTypes.contains(.reminder) else {
            throw ApiError.aliasBroken
        }

        let reminder = EKReminder(eventStore: store)
        reminder.calendar = calendar
        reminder.title = command.title
        reminder.notes = command.notes
        // Already range-checked at the edge (`Limits.priorityRange`); EventKit fails the save
        // with `EKErrorPriorityIsInvalid` for anything outside 0…9, so the clamp is belt and
        // braces. (The header declares `NSUInteger`, but Swift imports the property as `Int`.)
        reminder.priority = min(max(command.priority, 0), 9)
        if let dueAt = command.dueAt {
            reminder.dueDateComponents = DueDate.components(for: dueAt, timeZone: timeZone)
        }

        do {
            try store.save(reminder, commit: true)
        } catch {
            throw EventKitErrorMapping.apiError(for: error)
        }

        // Best effort on purpose. The reminder now exists and the user can see it, so failing the
        // request would both lie about the state of their list and make a retry create a second
        // one. The cost of a lost mapping is that this id 404s on `complete` — until the next
        // `list`, which mints a fresh mapping the first time it sees the reminder again.
        try? identity.recordMapping(
            bridgeId: command.id,
            itemId: reminder.calendarItemIdentifier,
            externalId: reminder.calendarItemExternalIdentifier,
            alias: command.alias,
            at: clock.now
        )

        // Built from the validated command rather than re-read from EventKit: a read-back would
        // report EventKit's own interpretation of the due date, and the caller asked for an
        // instant, not for a wall-clock rendering of one.
        return ReminderSnapshot(
            id: command.id,
            alias: command.alias,
            title: command.title,
            notes: command.notes,
            dueAt: command.dueAt,
            priority: command.priority,
            isCompleted: false,
            completedAt: nil
        )
    }

    public func complete(id: BridgeID) async throws -> CompleteOutcome {
        let allowlist = await allowlistProvider()
        try prepare()

        // Every failure below is a 404, never a 403 or a 409. The id refers to something the
        // caller has no business knowing exists, and distinguishing "gone" from "not yours" would
        // turn the id space into an oracle for the contents of non-allowlisted lists.
        guard let itemId = try identity.itemId(for: id) else { throw ApiError.notFound }
        guard let reminder = store.calendarItem(withIdentifier: itemId) as? EKReminder else {
            throw ApiError.notFound
        }
        // Re-checked against the reminder's *current* calendar, not the one it was created in: a
        // reminder moved into a non-allowlisted list is no longer ours to touch.
        guard let calendarId = reminder.calendar?.calendarIdentifier,
              let alias = allowlist.alias(forCalendarId: calendarId)
        else {
            throw ApiError.notFound
        }

        // Idempotent no-op. Nothing is asserted about `completionDate`: the header says it can be
        // nil while `isCompleted` is true when another client did the completing.
        guard !reminder.isCompleted else {
            try? identity.touch(id, at: clock.now)
            return CompleteOutcome(id: id, alreadyCompleted: true)
        }

        reminder.isCompleted = true
        do {
            try store.save(reminder, commit: true)
        } catch {
            throw EventKitErrorMapping.apiError(for: error)
        }

        try? identity.recordMapping(
            bridgeId: id,
            itemId: itemId,
            externalId: reminder.calendarItemExternalIdentifier,
            alias: alias,
            at: clock.now
        )
        return CompleteOutcome(id: id, alreadyCompleted: false)
    }

    // MARK: - Preconditions

    /// Run at the top of every operation, on the queue.
    private func prepare() throws {
        if changes.consume() {
            // Everything fetched from this store before the change is invalid; the reset happens
            // here rather than in the notification so it cannot land mid-fetch.
            store.reset()
        }
        // Re-read every time. A grant revoked in System Settings takes effect immediately, and
        // this is the route that does not depend on the notification having been delivered.
        guard RemindersAccess.status().isUsable else { throw ApiError.remindersUnavailable }
    }

    /// Turns a binding into a live calendar, or fails closed.
    ///
    /// `calendarIdentifier` is explicitly not sync-proof (`EKCalendar.h`), so a nil here is the
    /// expected outcome of an iCloud full sync, not a corruption. The alias is marked `broken`
    /// so the setup UI can offer a human the chance to re-point it, and the request fails.
    private func resolveCalendar(_ entry: AllowlistEntry) throws -> EKCalendar {
        guard let calendar = store.calendar(withIdentifier: entry.calendarId) else {
            try? identity.markAliasBroken(entry.alias)
            throw ApiError.aliasBroken
        }
        return calendar
    }

    /// The id a fetched reminder should be reported under.
    ///
    /// Reminders the bridge did not create — anything the user typed into Reminders.app — have no
    /// mapping yet. Minting one on first sight is what makes them completable; the alternative is
    /// a `list` that only ever shows the bridge's own reminders. The write is local metadata
    /// only: no EventKit state is changed by a GET.
    private func bridgeId(for reminder: RawReminder, alias: Alias, now: Date) -> BridgeID {
        if let existing = try? identity.bridgeId(forItemId: reminder.itemId) {
            // Keeps the pruning clock alive for a mapping EventKit still resolves.
            try? identity.touch(existing, at: now)
            return existing
        }
        let minted = BridgeID.generate()
        try? identity.recordMapping(
            bridgeId: minted,
            itemId: reminder.itemId,
            externalId: reminder.externalId,
            alias: alias,
            at: now
        )
        return minted
    }

    // MARK: - Fetching

    /// Wraps `fetchReminders(matching:completion:)`, which has no `NSError` parameter at all: on
    /// failure it hands back a nil array and says nothing.
    ///
    /// A nil is therefore mapped to **failure**, never to an empty list. The difference matters —
    /// "you have no reminders" and "the query failed" would otherwise be the same answer, and an
    /// agent acting on the first one deletes the wrong thing.
    ///
    /// The handle is a local of this frame rather than something a closure captures, so no
    /// `@Sendable` closure ever holds an EventKit value; `FetchGate` guarantees the continuation
    /// resumes exactly once no matter which of the three racers gets there first.
    private func fetch(matching predicate: NSPredicate) async throws -> [RawReminder] {
        let gate = FetchGate()

        let handle = store.fetchReminders(matching: predicate) { reminders in
            guard let reminders else {
                gate.finish(.failure(ApiError.remindersUnavailable))
                return
            }
            // Mapped here, inside the completion: this is the only point at which an `EKReminder`
            // is read, and a `RawReminder` is the only thing allowed out.
            gate.finish(.success(reminders.map(RawReminder.init)))
        }

        // EventKit provides no timeout of its own; without one a wedged sync would hold the
        // request — and this actor's queue — open indefinitely.
        //
        // `detached`, not `Task {}`: a child task would inherit this actor's isolation and so
        // would have to wait for the serial queue, which a concurrent blocking `save` can hold
        // for the length of an iCloud round trip. The timeout would then fire late, exactly when
        // it is most needed. It captures nothing but the gate and a `Duration`, both `Sendable`.
        let timeout = Task.detached { [fetchTimeout] in
            try? await Task.sleep(for: fetchTimeout)
            guard !Task.isCancelled else { return }
            gate.finish(.failure(ApiError.remindersUnavailable))
        }
        defer { timeout.cancel() }

        do {
            return try await withTaskCancellationHandler {
                try await gate.value
            } onCancel: {
                gate.finish(.failure(CancellationError()))
            }
        } catch {
            // Back on the actor, with `handle` still in scope. Cancelling an already-finished
            // fetch is documented as harmless, so this runs on the timeout path too.
            store.cancelFetchRequest(handle)
            throw EventKitErrorMapping.apiError(for: error)
        }
    }
}
