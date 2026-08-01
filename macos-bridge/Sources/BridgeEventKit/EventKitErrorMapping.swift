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
    static func apiError(for error: any Error) -> ApiError {
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
            return .remindersUnavailable

        // The name resolved to a real list, but that list cannot take this reminder. A 409
        // `list_read_only` says exactly that: the list exists, so retrying against it is pointless
        // and the caller has to pick a different one.
        case .calendarReadOnly, .calendarIsImmutable, .calendarDoesNotAllowReminders,
             .sourceDoesNotAllowReminders:
            return .listReadOnly

        // `EKErrorNoCalendar` means the item has no calendar at all — the list resolution the
        // handler did has come apart underneath it, which is the same thing the caller sees when
        // the name matches nothing.
        case .noCalendar:
            return .noSuchList

        // Rejections of the request's own content. These should all have been caught at the edge
        // by `Limits`/`Validate`; reaching one means the edge and EventKit disagree, and the
        // honest answer is still "your request", not "our fault".
        case .priorityIsInvalid, .noStartDate, .datesInverted, .recurringReminderRequiresDueDate,
             .startDateTooFarInFuture:
            return .invalidRequest

        default:
            return .internal
        }
    }
}
