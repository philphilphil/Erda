import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

func alias(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> Alias {
    try #require(Alias(rawValue: raw), sourceLocation: sourceLocation)
}

func allowlistEntry(
    _ raw: String,
    calendarId: String? = nil,
    state: AllowlistState = .ok
) throws -> AllowlistEntry {
    AllowlistEntry(
        alias: try alias(raw),
        calendarId: calendarId ?? "cal-\(raw)",
        titleAtBind: "List \(raw)",
        sourceAtBind: "iCloud",
        boundAt: Date(timeIntervalSince1970: 1_780_000_000),
        state: state
    )
}

/// `EKErrorDomain` and its ordinals, written out rather than imported.
///
/// The test target deliberately does **not** `import EventKit` — `BridgeEventKit` is the only
/// module allowed to — so these are transcribed from
/// `MacOSX.sdk/…/EventKit.framework/Headers/EKError.h`. Transcribing them is the stronger
/// assertion anyway: it pins the integers EventKit actually puts in an `NSError`, so a mapping
/// that silently stopped matching would fail here rather than in production.
enum EKErrorFixture {
    static let domain = "EKErrorDomain"

    static let noCalendar = 1
    static let noStartDate = 2
    static let datesInverted = 4
    static let internalFailure = 5
    static let calendarReadOnly = 6
    static let startDateTooFarInFuture = 9
    static let calendarIsImmutable = 16
    static let recurringReminderRequiresDueDate = 18
    static let calendarDoesNotAllowReminders = 23
    static let sourceDoesNotAllowReminders = 24
    static let priorityIsInvalid = 26
    static let eventStoreNotAuthorized = 29

    static func error(_ code: Int) -> NSError {
        NSError(
            domain: domain,
            code: code,
            // A realistic payload: the mapper must never let any of this reach the wire.
            userInfo: [
                NSLocalizedDescriptionKey: "The operation couldn’t be completed.",
                NSFilePathErrorKey: "/Users/somebody/Library/Calendars/x.caldav",
            ]
        )
    }
}
