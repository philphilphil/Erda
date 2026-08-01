import BridgeCore
import EventKit
import Foundation

/// Translation of anything EventKit can throw into the closed `ApiError` set.
///
/// The default is `.internal`, not a passthrough: an `NSError` from EventKit carries a
/// `localizedDescription` and often a file path in `userInfo`, and the wire format has no field
/// that could carry either. Every code below is one the header documents; everything else
/// collapses to a bare 500.
enum EventKitErrorMapping {
    /// Which half of the API the failure came from.
    ///
    /// It has to be passed in, because several `EKError` codes are shared by both and mean
    /// different things to a caller. `EKErrorCalendarReadOnly` is `list_read_only` for a reminder
    /// and `calendar_read_only` for an event; `EKErrorEventStoreNotAuthorized` is
    /// `reminders_unavailable` or `calendar_unavailable`, and telling Phil to check the wrong
    /// permission is a genuinely wasted trip to System Settings. The `NSError` itself says nothing
    /// about which entity was being written — only the call site knows.
    enum Entity: Sendable {
        case reminder
        case event
    }

    static func apiError(for error: any Error, entity: Entity) -> ApiError {
        // Errors this target raised itself already carry their own meaning.
        if let apiError = error as? ApiError { return apiError }
        if error is CancellationError { return .internal }

        let nsError = error as NSError
        guard nsError.domain == EKErrorDomain else { return .internal }

        switch EKError.Code(rawValue: nsError.code) {
        // Access was revoked between the authorization check and the call. The
        // `EKEventStoreChanged` notification is not guaranteed to have arrived first, so this is
        // the second, independent route to the same 503 — never a 500.
        case .eventStoreNotAuthorized:
            return entity == .reminder ? .remindersUnavailable : .calendarUnavailable

        // The name resolved to a real collection, but it cannot take this item. A 409 says exactly
        // that: it exists, so retrying against it is pointless and the caller has to pick another.
        case .calendarReadOnly, .calendarIsImmutable:
            return entity == .reminder ? .listReadOnly : .calendarReadOnly
        case .calendarDoesNotAllowReminders, .sourceDoesNotAllowReminders:
            return .listReadOnly
        // A subscribed or holiday calendar, and an account that does not hold events at all.
        case .calendarDoesNotAllowEvents, .sourceDoesNotAllowEvents:
            return .calendarReadOnly

        // `EKErrorNoCalendar` means the item has no calendar at all — the resolution the handler
        // did has come apart underneath it, which is the same thing the caller sees when the name
        // matches nothing.
        case .noCalendar:
            return entity == .reminder ? .noSuchList : .noSuchCalendar

        // Rejections of the request's own content. These should all have been caught at the edge
        // by `Limits`/`Validate`; reaching one means the edge and EventKit disagree, and the
        // honest answer is still "your request", not "our fault".
        case .priorityIsInvalid, .noStartDate, .noEndDate, .datesInverted,
             .recurringReminderRequiresDueDate, .startDateTooFarInFuture:
            return .invalidRequest

        default:
            return .internal
        }
    }
}
