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
public protocol RemindersService: Sendable {
    /// Cheap enough to call on every request; anything but `.ok` short-circuits to 503.
    func availability() async -> ReminderAvailability

    func list(_ query: ListRemindersQuery) async throws -> [ReminderSnapshot]

    func create(_ command: CreateReminderCommand) async throws -> ReminderSnapshot

    /// Completing an already-completed reminder succeeds as a no-op.
    func complete(id: BridgeID) async throws -> CompleteOutcome
}
