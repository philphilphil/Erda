import Foundation

/// An in-memory `RemindersService` that reproduces the real one's *contract*: fail-closed alias
/// resolution, 404 on an unknown id, complete-as-no-op, and a switchable availability.
///
/// It lives in the library, not in `BridgeCoreTests`, on purpose. A test target's types are not
/// importable from another module, and this fake has two consumers outside its own tests: the
/// `BridgeHTTP` socket tests (M3), and the running app during M3's "exercise the whole API from
/// `leela` against fake data before EventKit exists" step — which needs the fake linked into the
/// signed bundle. Neither is possible if it lives in a test target.
public actor FakeReminders: RemindersService {
    private var allowedAliases: Set<Alias>
    private var brokenAliases: Set<Alias>
    private var stored: [BridgeID: ReminderSnapshot] = [:]
    private var currentAvailability: ReminderAvailability
    /// When set, every call throws this instead of doing anything — for exercising error paths.
    private var forcedError: ApiError?

    public init(
        aliases: Set<Alias> = [],
        brokenAliases: Set<Alias> = [],
        availability: ReminderAvailability = .ok,
        seeded: [ReminderSnapshot] = []
    ) {
        self.allowedAliases = aliases
        self.brokenAliases = brokenAliases
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

    public func markBroken(_ alias: Alias) {
        brokenAliases.insert(alias)
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

    public func list(_ query: ListRemindersQuery) async throws -> [ReminderSnapshot] {
        try preflight()
        // An empty alias list means "every healthy alias", never "every list on the Mac".
        let requested = query.aliases.isEmpty ? allowedAliases.subtracting(brokenAliases) : Set(query.aliases)
        for alias in requested {
            try check(alias)
        }
        return stored.values
            .filter { requested.contains($0.alias) && !$0.isCompleted }
            .sorted { $0.id.rawValue < $1.id.rawValue }
            .prefix(query.limit)
            .map { $0 }
    }

    public func create(_ command: CreateReminderCommand) async throws -> ReminderSnapshot {
        try preflight()
        try check(command.alias)
        let snapshot = ReminderSnapshot(
            id: command.id,
            alias: command.alias,
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
        // A reminder whose list has since gone broken is a 404, not a 409: the caller has no
        // business learning that the id exists.
        guard allowedAliases.contains(existing.alias), !brokenAliases.contains(existing.alias) else {
            throw ApiError.notFound
        }
        guard !existing.isCompleted else { return CompleteOutcome(id: id, alreadyCompleted: true) }

        stored[id] = ReminderSnapshot(
            id: existing.id,
            alias: existing.alias,
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

    private func check(_ alias: Alias) throws {
        guard allowedAliases.contains(alias) else { throw ApiError.aliasUnknown }
        guard !brokenAliases.contains(alias) else { throw ApiError.aliasBroken }
    }
}
