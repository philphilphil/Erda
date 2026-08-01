import Foundation

/// An in-memory `RemindersService` that reproduces the real one's *contract*: a name that matches
/// no list fails closed, an unknown id is a 404, complete is a no-op the second time, and
/// availability is switchable.
///
/// It lives in the library, not in `BridgeCoreTests`, on purpose. A test target's types are not
/// importable from another module, and this fake has two consumers outside its own tests: the
/// `BridgeHTTP` socket tests (M3), and the running app during M3's "exercise the whole API from
/// `leela` against fake data before EventKit exists" step — which needs the fake linked into the
/// signed bundle. Neither is possible if it lives in a test target.
public actor FakeReminders: RemindersService {
    /// The lists this pretend Mac has. Nothing outside it resolves, and there is no default.
    private var knownLists: Set<ListName>
    /// Lists that exist but cannot take a new reminder — Reminders' read-only shared lists.
    private var readOnlyLists: Set<ListName>
    private var stored: [BridgeID: ReminderSnapshot] = [:]
    private var currentAvailability: ReminderAvailability
    /// When set, every call throws this instead of doing anything — for exercising error paths.
    private var forcedError: ApiError?

    public init(
        lists: Set<ListName> = [],
        readOnly: Set<ListName> = [],
        availability: ReminderAvailability = .ok,
        seeded: [ReminderSnapshot] = []
    ) {
        self.knownLists = lists
        self.readOnlyLists = readOnly
        self.currentAvailability = availability
        self.stored = Dictionary(seeded.map { ($0.id, $0) }, uniquingKeysWith: { _, latest in latest })
    }

    // MARK: - Test controls

    public func setAvailability(_ availability: ReminderAvailability) {
        currentAvailability = availability
    }

    public func setForcedError(_ error: ApiError?) {
        forcedError = error
    }

    public func markReadOnly(_ list: ListName) {
        readOnlyLists.insert(list)
    }

    /// Simulates a list being deleted in Reminders.app while its reminders are still mapped.
    public func removeList(_ list: ListName) {
        knownLists.remove(list)
        readOnlyLists.remove(list)
    }

    public func seed(_ snapshot: ReminderSnapshot) {
        stored[snapshot.id] = snapshot
    }

    public var all: [ReminderSnapshot] {
        stored.values.sorted { $0.id.rawValue < $1.id.rawValue }
    }

    // MARK: - RemindersService

    public func availability() async -> ReminderAvailability {
        currentAvailability
    }

    public func availableLists() async -> [ListName] {
        currentAvailability == .ok ? knownLists.sorted() : []
    }

    public func list(_ query: ListRemindersQuery) async throws -> [ReminderSnapshot] {
        try preflight()
        // No name given means every list on this Mac — which is the whole behaviour change: with
        // no allowlist there is nothing narrower for it to mean.
        let requested = query.lists.isEmpty ? knownLists : Set(query.lists)
        for list in requested {
            try check(list)
        }
        return stored.values
            .filter { requested.contains($0.list) && !$0.isCompleted }
            .sorted { $0.id.rawValue < $1.id.rawValue }
            .prefix(query.limit)
            .map { $0 }
    }

    public func create(_ command: CreateReminderCommand) async throws -> ReminderSnapshot {
        try preflight()
        try check(command.list)
        guard !readOnlyLists.contains(command.list) else { throw ApiError.listReadOnly }

        let snapshot = ReminderSnapshot(
            id: command.id,
            list: command.list,
            title: command.title,
            notes: command.notes,
            dueAt: command.dueAt,
            priority: command.priority,
            isCompleted: false,
            completedAt: nil
        )
        stored[command.id] = snapshot
        return snapshot
    }

    public func complete(id: BridgeID) async throws -> CompleteOutcome {
        try preflight()
        guard let existing = stored[id] else { throw ApiError.notFound }
        // A reminder whose list has since gone is a 404, not a 409: the id no longer resolves to
        // anything that can be confirmed, and a dangling id must not quietly succeed.
        guard knownLists.contains(existing.list) else { throw ApiError.notFound }
        guard !existing.isCompleted else { return CompleteOutcome(id: id, alreadyCompleted: true) }

        stored[id] = ReminderSnapshot(
            id: existing.id,
            list: existing.list,
            title: existing.title,
            notes: existing.notes,
            dueAt: existing.dueAt,
            priority: existing.priority,
            isCompleted: true,
            completedAt: Date(timeIntervalSince1970: 0)
        )
        return CompleteOutcome(id: id, alreadyCompleted: false)
    }

    // MARK: - Internals

    private func preflight() throws {
        if let forcedError { throw forcedError }
        if let unavailable = currentAvailability.apiError { throw unavailable }
    }

    private func check(_ list: ListName) throws {
        guard knownLists.contains(list) else { throw ApiError.noSuchList }
    }
}
