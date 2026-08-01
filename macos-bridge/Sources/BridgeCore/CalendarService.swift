import Foundation

/// The calendar half of the seam, alongside `RemindersService` and for the same reasons.
///
/// Everything crossing it is a `Sendable` value type, because EventKit's headers carry no
/// `Sendable` annotations at all: `EKEvent`/`EKCalendar`/`EKEventStore` are non-`Sendable` class
/// types under Swift 6 and the compiler will refuse to let one cross an isolation boundary.
/// Mapping to these structs inside `BridgeEventKit` is what makes that enforceable, and what lets
/// the whole HTTP layer be tested on a machine with no calendars at all.
///
/// It is a **separate protocol** from `RemindersService` rather than more methods on it: macOS
/// authorizes events and reminders independently, so a bridge can perfectly well be able to serve
/// one and not the other, and one protocol would have no way to say so. The two are implemented by
/// the same actor (see `BridgeEventKit.EventKitStore`) because they must share one `EKEventStore`
/// — but that is an implementation fact, not something the request layer knows.
///
/// Implementations throw `ApiError` and nothing else — an `NSError` from EventKit is mapped at the
/// boundary, because its `localizedDescription` must never reach a response body.
///
/// ## Scope
///
/// Two operations: create an event, and read upcoming ones. There is deliberately no edit, no
/// delete, no recurrence, no attendees and no alarms — see `macos-bridge/README.md`'s threat model.
public protocol CalendarService: Sendable {
    /// Cheap enough to call on every request; anything but `.ok` short-circuits to 503.
    ///
    /// Named for its entity rather than just `availability()` so it cannot collide with
    /// `RemindersService.availability()` on a type conforming to both — two methods differing only
    /// in return type would compile, and would then make every call site an exercise in inference.
    func calendarAvailability() async -> CalendarAvailability

    /// The names a caller may address, for `GET /v1/status`. A readout rather than an operation:
    /// it answers `[]` when access is not usable instead of throwing, so the status route can keep
    /// its promise to always answer.
    func availableCalendars() async -> [CalendarName]

    /// Events starting inside the query's window, soonest first.
    func upcoming(_ query: ListCalendarEventsQuery) async throws -> [CalendarEventSnapshot]

    func create(_ command: CreateCalendarEventCommand) async throws -> CalendarEventSnapshot
}
