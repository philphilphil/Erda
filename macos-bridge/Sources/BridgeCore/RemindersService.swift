import Foundation

/// **The seam** (design dossier §6.2).
///
/// Everything crossing it is a `Sendable` value type. That is not stylistic: EventKit's headers
/// carry no `Sendable` annotations at all, so `EKReminder`/`EKCalendar`/`EKEventStore` are
/// non-`Sendable` class types under Swift 6 and the compiler will refuse to let one cross an
/// isolation boundary. Mapping to these structs inside `BridgeEventKit` is what makes that
/// enforceable, and what lets the whole HTTP layer be tested on a machine with no Reminders at all.
///
/// Implementations throw `ApiError` and nothing else — an `NSError` from EventKit is mapped at
/// the boundary, because its `localizedDescription` must never reach a response body.
///
/// ## Reads span everything; writes go to exactly one list
///
/// `list` may be filtered by name or left to span every reminder list on the Mac. `create` takes no
/// list at all: it lands in the single target a human pinned in the setup window (see `ListBinding`),
/// and fails closed with `listNotConfigured` when there is none or the pinned one no longer resolves.
/// Never a default list, and never a re-bind by title. This mirrors the calendar half exactly — the
/// two capabilities pin, and fail, independently.
public protocol RemindersService: Sendable {
    /// Cheap enough to call on every request; anything but `.ok` short-circuits to 503.
    func availability() async -> ReminderAvailability

    /// The names a caller may address in a **read** filter, for `GET /v1/status`. A readout rather
    /// than an operation: it answers `[]` when access is not usable instead of throwing, so the
    /// status route can keep its promise to always answer.
    func availableLists() async -> [ListName]

    /// The pinned write target, for `GET /v1/status`. A readout like `availableLists()`: it answers
    /// `.notConfigured`/`.unresolvable` rather than throwing, so the status route can keep its
    /// promise to always answer.
    func writeList() async -> WriteListReport

    /// Incomplete reminders, soonest due first. Spans every list unless the query names some.
    func list(_ query: ListRemindersQuery) async throws -> [ReminderSnapshot]

    /// Creates the reminder in the pinned write list — the command names none, and there is nothing
    /// here to steer it with. Throws `listNotConfigured` when none is pinned or the pinned one no
    /// longer resolves; the returned snapshot reports which list it landed in.
    func create(_ command: CreateReminderCommand) async throws -> ReminderSnapshot

    /// Completing an already-completed reminder succeeds as a no-op.
    func complete(id: BridgeID) async throws -> CompleteOutcome
}
