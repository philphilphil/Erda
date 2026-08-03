import BridgeCore
import Foundation

/// Where the one reminder list the bridge writes to lives.
///
/// The reminder counterpart of `CalendarBindingRepository`: two `meta` rows, in the same table, with
/// the same posture and for the same reason — this is a choice a human makes locally, once, and **no
/// route can change it**. The keys are distinct from the calendar binding's (`list_id`/`list_title`
/// versus `calendar_id`/`calendar_title`), so the two bindings coexist in the shared table without
/// stomping each other.
///
/// **There is deliberately no default and no auto-pick.** A missing or half-written pair reads back
/// as `nil`, and a `nil` makes `POST /v1/reminders` answer `503 list_not_configured`. Falling back
/// to a default list would be worse than failing: it is whatever Reminders.app last decided, it
/// changes without anyone noticing, and the first sign of it being wrong is a task in a shared list.
public struct ListBindingRepository: Sendable {
    static let idKey = "list_id"
    static let titleKey = "list_title"

    private let meta: MetaRepository

    public init(meta: MetaRepository) {
        self.meta = meta
    }

    /// `nil` when no complete binding has been stored.
    ///
    /// A row with an identifier but no title — or a title that is no longer a usable `ListName` — is
    /// treated as absent rather than repaired: a binding that cannot be *displayed* cannot be
    /// confirmed by a human either, and an unconfirmable write target is one the bridge has no
    /// business writing to.
    public func load() throws -> ListBinding? {
        guard let listId = try meta.value(for: Self.idKey), !listId.isEmpty,
              let rawTitle = try meta.value(for: Self.titleKey),
              let title = ListName(rawValue: rawTitle)
        else { return nil }
        return ListBinding(listId: listId, titleAtBind: title)
    }

    /// Stores a choice verbatim. Whether the identifier still resolves is decided at every write,
    /// against EventKit — a "verified" flag on disk would be a lie with a timestamp, for the same
    /// reason `CalendarBindingRepository` keeps none.
    public func save(_ binding: ListBinding) throws {
        try meta.set(binding.listId, for: Self.idKey)
        try meta.set(binding.titleAtBind.rawValue, for: Self.titleKey)
    }

    public func clear() throws {
        try meta.remove(Self.idKey)
        try meta.remove(Self.titleKey)
    }
}
