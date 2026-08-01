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
///
/// ## Scope
///
/// Every reminder list on this Mac is reachable. There is no allowlist — a deliberate decision,
/// not an oversight: Apple grants reminder access all-or-nothing, and the alias table that used to
/// sit here bounded nothing Phil wanted bounded. Lists are addressed by name, and a name that
/// matches nothing (or matches two lists) fails rather than falling back to a default.
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
    private let identity: any ReminderIdentityStore
    private let clock: any BridgeClock
    /// The zone a due date is *expressed in* once it reaches Reminders.app. The wire format
    /// requires an offset-bearing timestamp, so the instant is already unambiguous; this only
    /// decides which wall-clock time the user sees.
    private let timeZone: TimeZone
    private let fetchTimeout: Duration
    private let changes: EventStoreChangeFlag

    public init(
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
        RemindersAccess.status().isUsable ? .ok : .unauthorized
    }

    /// The list names a caller may address. Never throws: `GET /v1/status` has to answer even when
    /// the answer is "nothing, because access was revoked".
    public func availableLists() async -> [ListName] {
        guard RemindersAccess.status().isUsable else { return [] }
        if changes.consume() { store.reset() }
        // Deduplicated: two accounts can hold a same-named list, and reporting the name twice
        // would suggest they are separately addressable when in fact the name is ambiguous and
        // resolves to neither.
        return Set(candidates().compactMap(ListLookup.canonicalName)).sorted()
    }

    public func list(_ query: ListRemindersQuery) async throws -> [ReminderSnapshot] {
        try prepare()

        let all = store.calendars(for: .reminder)
        var calendars: [EKCalendar] = []
        var nameByCalendarId: [String: ListName] = [:]

        if query.lists.isEmpty {
            // No name given now means *every* reminder list — the behaviour change the removal of
            // the allowlist is. The array is still assembled explicitly rather than left empty:
            // `predicateForReminders(in:)` documents nil/empty as "every calendar", which is a
            // different and much wider thing than "every reminder list".
            for calendar in all {
                guard let name = ListLookup.canonicalName(candidate(calendar)) else { continue }
                calendars.append(calendar)
                nameByCalendarId[calendar.calendarIdentifier] = name
            }
        } else {
            let candidates = all.map(candidate)
            for requested in query.lists {
                let match = try ListLookup.resolve(requested, in: candidates)
                // Two spellings of the same list must not become the same calendar twice.
                guard nameByCalendarId[match.calendarId] == nil else { continue }
                guard let calendar = all.first(where: { $0.calendarIdentifier == match.calendarId }),
                      let name = ListLookup.canonicalName(match)
                else { throw ApiError.noSuchList }
                calendars.append(calendar)
                nameByCalendarId[match.calendarId] = name
            }
        }

        // Nothing to read from is an empty answer, not an error and — critically — not a fetch
        // with an empty calendar array, which EventKit would read as "everything".
        guard !calendars.isEmpty else { return [] }

        // NOT `predicateForIncompleteReminders(withDueDateStarting:ending:calendars:)`. Its
        // nil/nil window is documented as "all incomplete reminders" but the header does not say
        // whether a reminder with no due date falls inside a date window at all, and silently
        // dropping every undated reminder is exactly the kind of bug nobody notices for months.
        // Fetching everything in the chosen calendars and filtering `!isCompleted` in Swift is
        // slower and provably right.
        let raw = try await fetch(matching: store.predicateForReminders(in: calendars))

        let now = clock.now
        var snapshots: [ReminderSnapshot] = []
        for reminder in raw where !reminder.isCompleted {
            // Defence in depth: the predicate already restricted the fetch to these calendars,
            // so anything else here would mean EventKit ignored it.
            guard let calendarId = reminder.calendarId, let name = nameByCalendarId[calendarId] else {
                continue
            }
            snapshots.append(reminder.snapshot(id: bridgeId(for: reminder, list: name, now: now), list: name))
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
        try prepare()

        let calendar = try resolveCalendar(named: command.list)
        // A list that resolves but cannot hold a reminder — read-only, or an account that does not
        // do reminders — is a 409 rather than an `EKErrorCalendarReadOnly` round trip. It is the
        // caller's list choice that is wrong, and no retry against this list will ever work.
        guard calendar.allowsContentModifications, calendar.allowedEntityTypes.contains(.reminder) else {
            throw ApiError.listReadOnly
        }
        // The list's own spelling, so a caller who matched case-insensitively is told which list
        // it actually landed in.
        let name = ListLookup.canonicalName(candidate(calendar)) ?? command.list

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
            list: name,
            at: clock.now
        )

        // Built from the validated command rather than re-read from EventKit: a read-back would
        // report EventKit's own interpretation of the due date, and the caller asked for an
        // instant, not for a wall-clock rendering of one.
        return ReminderSnapshot(
            id: command.id,
            list: name,
            title: command.title,
            notes: command.notes,
            dueAt: command.dueAt,
            priority: command.priority,
            isCompleted: false,
            completedAt: nil
        )
    }

    public func complete(id: BridgeID) async throws -> CompleteOutcome {
        try prepare()

        // Every failure below is a 404, never a 409. A mapping row outlives the reminder it points
        // at — `calendarItemIdentifier` is not sync-proof — so "the id no longer resolves to a
        // reminder in a list that exists" is the ordinary outcome, and it must not be dressed up
        // as a success.
        guard let itemId = try identity.itemId(for: id) else { throw ApiError.notFound }
        guard let reminder = store.calendarItem(withIdentifier: itemId) as? EKReminder else {
            throw ApiError.notFound
        }
        // Re-checked against the reminder's *current* calendar, not the one it was created in: a
        // reminder that has been moved, or whose list was deleted, must not complete silently on
        // the strength of a stale row.
        guard let calendarId = reminder.calendar?.calendarIdentifier,
              let name = ListLookup.name(forCalendarId: calendarId, in: candidates())
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
            list: name,
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

    /// This Mac's reminder lists, read fresh: one can be created, renamed or deleted in
    /// Reminders.app between two requests, and a cached snapshot would resolve a name to a list
    /// that is no longer the one wearing it.
    private func candidates() -> [ListLookup.Candidate] {
        store.calendars(for: .reminder).map(candidate)
    }

    private func candidate(_ calendar: EKCalendar) -> ListLookup.Candidate {
        ListLookup.Candidate(calendarId: calendar.calendarIdentifier, title: calendar.title)
    }

    /// Turns a name into a live calendar, or fails closed. Never a default list.
    private func resolveCalendar(named name: ListName) throws -> EKCalendar {
        let all = store.calendars(for: .reminder)
        let match = try ListLookup.resolve(name, in: all.map(candidate))
        guard let calendar = all.first(where: { $0.calendarIdentifier == match.calendarId }) else {
            throw ApiError.noSuchList
        }
        return calendar
    }

    /// The id a fetched reminder should be reported under.
    ///
    /// Reminders the bridge did not create — anything the user typed into Reminders.app — have no
    /// mapping yet. Minting one on first sight is what makes them completable; the alternative is
    /// a `list` that only ever shows the bridge's own reminders. The write is local metadata
    /// only: no EventKit state is changed by a GET.
    private func bridgeId(for reminder: RawReminder, list: ListName, now: Date) -> BridgeID {
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
            list: list,
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
