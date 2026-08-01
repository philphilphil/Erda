import Foundation

public enum AllowlistState: String, Sendable, Hashable, CaseIterable, Codable {
    case ok
    /// The bound `calendarIdentifier` no longer resolves. An iCloud full sync loses those ids
    /// (`EKCalendar.h`), and re-binding by title is exactly how you would start writing into a
    /// stranger's shared list — so a broken alias fails closed until a human re-binds it locally.
    case broken
}

/// One binding of an alias to a Reminders list, as stored by `BridgeStore` (M2).
public struct AllowlistEntry: Sendable, Equatable {
    public let alias: Alias
    public let calendarId: String
    public let titleAtBind: String
    public let sourceAtBind: String
    public let boundAt: Date
    public let state: AllowlistState

    public init(
        alias: Alias,
        calendarId: String,
        titleAtBind: String,
        sourceAtBind: String,
        boundAt: Date,
        state: AllowlistState = .ok
    ) {
        self.alias = alias
        self.calendarId = calendarId
        self.titleAtBind = titleAtBind
        self.sourceAtBind = sourceAtBind
        self.boundAt = boundAt
        self.state = state
    }
}

/// Resolution of aliases to lists. **Fails closed in every direction**: an alias that is not in
/// the table resolves to `nil`, never to a default, and there is no fallback lookup by title.
public struct Allowlist: Sendable, Equatable {
    private let entries: [Alias: AllowlistEntry]

    public init(entries: [AllowlistEntry]) {
        self.entries = Dictionary(entries.map { ($0.alias, $0) }, uniquingKeysWith: { _, latest in latest })
    }

    public var isEmpty: Bool { entries.isEmpty }

    /// Aliases currently usable, sorted for stable output.
    public var healthyAliases: [Alias] {
        entries.values.filter { $0.state == .ok }.map(\.alias).sorted()
    }

    public var brokenAliases: [Alias] {
        entries.values.filter { $0.state == .broken }.map(\.alias).sorted()
    }

    /// The raw lookup. Returns the entry whatever its state, or `nil` if the alias is unknown.
    public func entry(for alias: Alias) -> AllowlistEntry? {
        entries[alias]
    }

    /// The lookup handlers use: unknown ⇒ `alias_unknown`, broken ⇒ `alias_broken`, never a default.
    public func resolve(_ alias: Alias) throws -> AllowlistEntry {
        guard let entry = entries[alias] else { throw ApiError.aliasUnknown }
        guard entry.state == .ok else { throw ApiError.aliasBroken }
        return entry
    }

    /// Same lookup with no error distinction, for callers that only need "usable or not".
    public func resolveHealthy(_ alias: Alias) -> AllowlistEntry? {
        guard let entry = entries[alias], entry.state == .ok else { return nil }
        return entry
    }

    /// Availability contribution: an authorized store with nothing healthy to talk to is unusable.
    public func availability(authorized: Bool) -> ReminderAvailability {
        guard authorized else { return .unauthorized }
        return healthyAliases.isEmpty ? .noAllowlist : .ok
    }
}
